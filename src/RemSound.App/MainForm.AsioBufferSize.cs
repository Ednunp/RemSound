using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// THE ASIO BUFFER SIZE, per profile, beside the ASIO driver choice on the Audio inputs and outputs tab (Ed, 2026-09-28:
/// out of the latency lab, where it was one machine-wide switch). "Driver's own" - the default, and what RemSound always
/// did - asks the driver for nothing; 32 to 512 samples ask for that size, fitted to what the driver allows, when the
/// driver opens (<see cref="SharedAsioDevice.RequestedBufferFrames"/>, <see cref="AsioBufferSizing"/>). A change reopens
/// a driver that is open. ASIO only: a WASAPI device in shared mode runs at the engine's own period, which no application
/// chooses, so there is nothing for this to set there; with both kinds of device it is the ASIO side's.
/// </summary>
public sealed partial class MainForm
{
    private readonly ComboBox asioBufferSizeBox = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = Dpi.Px(200),
        AccessibleName = "ASIO buffer size (Alt+X)",
    };
    private MnemonicLabel? asioBufferSizeLabel;
    // A reopen waits for the choice to settle, as the driver choice does: arrowing through the sizes must not close and
    // open a single-client driver at every step.
    private readonly System.Windows.Forms.Timer asioBufferReopenDebounce = new() { Interval = 300 };
    private bool suppressAsioBufferSizeChange;

    /// <summary>Start-up, before the driver first opens: the list, the profile's size, what the open will ask for, and a
    /// word in the log if the latency lab's old machine-wide size was set.</summary>
    private void WireAsioBufferSize()
    {
        asioBufferSizeBox.Items.AddRange(AsioBufferSizing.Choices.Select(f => (object)AsioBufferSizing.Describe(f)).ToArray());
        var frames = settings.LoadAsioBufferFrames();
        suppressAsioBufferSizeChange = true;
        try { asioBufferSizeBox.SelectedIndex = Math.Max(0, Array.IndexOf(AsioBufferSizing.Choices, frames)); }
        finally { suppressAsioBufferSizeChange = false; }
        SharedAsioDevice.RequestedBufferFrames = frames;
        logFile.Event($"ASIO buffer size at start: {AsioBufferSizing.Describe(frames)} (this profile)");

        // The lab's size was machine-wide; profiles now start at the driver's own. Said once, then forgotten.
        var cfg = AppConfig.Load();
        if (cfg.LabAsioBufferFrames != 0)
        {
            logFile.Event($"ASIO buffer size: the latency lab's machine-wide {cfg.LabAsioBufferFrames} frames no longer applies - the size is "
                          + $"a profile setting now, beside the ASIO driver, and this profile asks for {AsioBufferSizing.Describe(frames)}");
            cfg.LabAsioBufferFrames = 0;
            try { cfg.Save(); } catch { /* the line comes again next start, nothing worse */ }
        }

        asioBufferSizeBox.SelectedIndexChanged += (_, _) =>
        {
            if (suppressAsioBufferSizeChange || asioBufferSizeBox.SelectedIndex < 0) return;
            var chosen = AsioBufferSizing.Choices[asioBufferSizeBox.SelectedIndex];
            if (chosen == settings.LoadAsioBufferFrames()) return;
            settings.SaveAsioBufferFrames(chosen);
            SharedAsioDevice.RequestedBufferFrames = chosen;
            LogUiChange("ASIO buffer size", AsioBufferSizing.Describe(chosen));
            MarkProfileDirty();
            asioBufferReopenDebounce.Stop();
            asioBufferReopenDebounce.Start();
        };
        asioBufferReopenDebounce.Tick += (_, _) =>
        {
            asioBufferReopenDebounce.Stop();
            ReopenAsioForBufferSize();
        };
        // A timer is a component the form's own Dispose never reaches, and the window is rebuilt on every profile switch.
        Disposed += (_, _) => { asioBufferReopenDebounce.Stop(); asioBufferReopenDebounce.Dispose(); };
        ContextHelp.Mark(asioBufferSizeBox, "audio-io.asio-buffer-size");
    }

    /// <summary>The Audio inputs and outputs tab's ASIO driver cell: the driver list, and the buffer size under it.</summary>
    private Control AsioDriverCell()
    {
        asioBufferSizeLabel = new MnemonicLabel
        {
            Text = "ASIO buffer size (Alt+&X):",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            MnemonicTarget = asioBufferSizeBox,
        };
        var bufferRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Dpi.Pad(0, 4, 0, 0) };
        bufferRow.Controls.Add(asioBufferSizeLabel);
        bufferRow.Controls.Add(asioBufferSizeBox);
        var cell = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        cell.Controls.Add(asioDriverBox);
        cell.Controls.Add(bufferRow);
        return cell;
    }

    /// <summary>The profile's size as the next open will ask for it (the gate reads it).</summary>
    internal int AsioBufferFramesForTest => settings.LoadAsioBufferFrames();

    /// <summary>The audio configuration the window's log lines are stamped with (the gate reads it).</summary>
    internal AudioConfiguration ActiveAudioConfigurationForTest() => ActiveAudioConfiguration();

    /// <summary>
    /// Close the ASIO driver and open it again, so a new buffer size is asked for. The same stop / rebuild / start the
    /// wake-from-sleep restart uses (RestartAudioAfterWake), without its tune restore: the sender is bounced through
    /// WASAPI-only so the shared driver is really released, ApplyAsioMode rebuilds both sides, and whatever was running
    /// is started again. The tuner is held while the devices settle. With no ASIO driver in use the new size simply
    /// applies the next time one opens. A headless (gate) window touches no real driver.
    /// </summary>
    private void ReopenAsioForBufferSize()
    {
        if (IsDisposed) return;
        var mode = settings.LoadAudioMode();
        var driver = settings.LoadAsioDriverName();
        var size = AsioBufferSizing.Describe(SharedAsioDevice.RequestedBufferFrames);
        if (mode == AudioMode.WasapiOnly || string.IsNullOrWhiteSpace(driver))
        {
            logFile.Event($"ASIO buffer size {size}: no ASIO driver in use, so it applies the next time one opens [{ActiveAudioConfiguration().Describe()}]");
            return;
        }
        if (headless)
        {
            logFile.Event($"ASIO buffer size {size}: a headless window touches no real driver; it applies at the next open [{ActiveAudioConfiguration().Describe()}]");
            return;
        }
        logFile.Event($"ASIO buffer size {size}: reopening \"{driver}\" to ask for it [{ActiveAudioConfiguration().Describe()}]");
        var splash = AsioLoadingSplash.StartIfAsioDriverName(driver, "Reopening the audio driver, please wait...");
        try
        {
            var wasReceiving = receiver.IsRunning;
            var wasSending = sender.IsRunning;
            // The metronome may be holding the outputs open on its own (nobody connected): let go, and hold again after.
            var metronomeHeld = receiver.OutputsHeldForMetronome;
            receiver.SetOutputsForMetronome(false);
            receiver.SetPlaybackEnabled(false);
            if (wasSending) sender.Stop();
            tuneHold.HoldAll(DateTime.UtcNow + TuneEvidenceHold.AfterBackendReinit);
            try { sender.SetAudioMode(AudioMode.WasapiOnly, null); }
            catch (Exception ex) { logFile.Event($"ASIO buffer size: sender WasapiOnly bounce failed: {ex.GetType().Name}: {ex.Message}"); }
            ClearAsioProbeCache();
            ApplyAsioMode();
            if (wasReceiving && !receiver.IsRunning)
            {
                ApplyReceiveDevices(starting: true);
                PushAllowedReceiveSenders();
                receiver.SetPlaybackEnabled(true);
            }
            if (wasSending && !sender.IsRunning)
            {
                ApplySendSources();
                sender.Start();
            }
            if (metronomeHeld)
            {
                ApplyReceiveDevices(starting: true);
                receiver.SetOutputsForMetronome(true);
            }
            logFile.Event($"ASIO buffer size: ASIO reopened (receiving={receiver.IsRunning}, sending={sender.IsRunning}) [{ActiveAudioConfiguration().Describe()}]");
        }
        catch (Exception ex)
        {
            logFile.Event($"ASIO buffer size: reopening ASIO failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            splash?.Dismiss();
        }
    }
}
