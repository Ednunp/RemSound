using System.Net;
using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// THE METRONOME, the window's part (Ed, 2026-09-28: "a metronome menu with a start stop that later becomes mute unmute,
/// when master metronome is playing. start stop is also a global hotkey. then another box for metronome settings"). The
/// metronome itself is <see cref="MetronomeEngine"/>; this gives it a menu, a settings window, a hotkey and control-channel
/// commands, seals and sends its messages and reads the ones that arrive, asks when someone wants to be the master, and
/// puts its clicks where the settings say: RemSound's own outputs (held open for it alone when nothing else is playing),
/// never an output this profile sends a loopback of, or the DAW. PLAN-metronome-2026-09-28.md holds every decision.
/// </summary>
public sealed partial class MainForm
{
    private MetronomeEngine metronome = null!;
    private MetronomeSettings metronomeSettings = new();
    private readonly MetronomeReplayGuard metronomeGuard = new();
    private readonly System.Windows.Forms.Timer metronomeTimer = new() { Interval = 50 };
    private ToolStripMenuItem metronomeMenu = null!;
    private ToolStripMenuItem metronomeStartStopItem = null!;
    private ToolStripMenuItem metronomeMasterItem = null!;
    private ToolStripMenuItem metronomeSettingsItem = null!;
    private uint metronomeSequence;
    private bool metronomeAskOpen;
    private string metronomeLoopbackSignature = "";
    private readonly Dictionary<IPAddress, (long Second, int Count)> metronomeArrivals = [];

    /// <summary>A clock of the test's own for this window's metronome (two windows in one process, their clocks apart).</summary>
    internal static Func<long>? MetronomeClockForTest { get; set; }

    /// <summary>Set up once, in the constructor, before anything can arrive.</summary>
    private void InitMetronome()
    {
        metronome = new MetronomeEngine(MetronomeClockForTest, line => logFile.Event($"{line} [{ActiveAudioConfiguration().Describe()}]"))
        {
            Send = SendMetronomeMessage,
            SchedulePublished = ApplyMetronomeSchedule,
            Changed = UpdateMetronomeMenu,
            Peers = MetronomePeers,
            ThroughServer = ep => RelayGroupClient.IsMemberAddress(ep.Address) || relayGroup.RelayOf(ep) is not null,
            AskAboutOffer = offer => BeginInvokeIfOpen(() => AskAboutMetronomeOffer(offer)),
            StoppedFollowing = what => { ScreenReader.Speak(what); UpdateMetronomeMenu(); },
            MasterNotice = what => BeginInvokeIfOpen(() => { ScreenReader.Speak(what); UpdateMetronomeMenu(); }),
            // The server's metronome (2026-09-29): through a server, it keeps everyone's beat.
            OnServerNow = () => relayGroup.ConnectedRelay is { } relay && relayGroup.IsInGroup(relay),
            ToServer = body => relayGroup.SendTiming(body),
            ServerMemberName = id => relayGroup.Members.FirstOrDefault(m => m.Id == id)?.Name,
        };
        relayGroup.TimingReceived = (body, _) =>
        {
            if (ServerMetronomeWire.TryReadGrid(body, out var grid))
                BeginInvokeIfOpen(() => { metronome.OnServerGrid(grid); if (metronome.NeedsTicks) metronomeTimer.Enabled = true; });
        };
        metronomeTimer.Tick += (_, _) => MetronomeTimerTick();
        receiver.OnMetronomeReceived = OnMetronomeArrived;
    }

    private void BeginInvokeIfOpen(Action action)
    {
        try { BeginInvoke(action); }
        catch (ObjectDisposedException) { /* closing */ }
        catch (InvalidOperationException) { /* no handle yet */ }
    }

    /// <summary>The Metronome menu, between DAW plugin and Options. Alt+T (Ed, 2026-09-29; it was Alt+Z): M is taken (Rename
    /// peer, Total latency and EQ mode use it), and T came free when the auto-tune tick boxes moved to Alt+U.</summary>
    private ToolStripMenuItem BuildMetronomeMenu()
    {
        metronomeMenu = new ToolStripMenuItem("Metronome (Alt+&T)") { AccessibleName = "Metronome menu" };
        metronomeStartStopItem = new ToolStripMenuItem("&Start metronome") { AccessibleName = "Start metronome" };
        metronomeStartStopItem.Click += (_, _) => MetronomeStartStop("the menu");
        metronomeMasterItem = new ToolStripMenuItem("&Be the master metronome") { AccessibleName = "Be the master metronome", CheckOnClick = false };
        metronomeMasterItem.Click += (_, _) => SetMetronomeMaster(!metronomeMasterItem.Checked);
        metronomeSettingsItem = new ToolStripMenuItem("Metronome se&ttings...") { AccessibleName = "Metronome settings" };
        metronomeSettingsItem.Click += (_, _) => OpenMetronomeSettings();
        metronomeMenu.DropDownItems.AddRange(new ToolStripItem[] { metronomeStartStopItem, metronomeMasterItem, metronomeSettingsItem });
        // The item's words change (Start, Stop, Mute, Unmute); its help does not - it is marked as built, as Start recording is.
        UpdateMetronomeMenu();
        return metronomeMenu;
    }

    /// <summary>Start or Stop your own; Mute or Unmute your clicks while you follow a master (you cannot stop theirs).</summary>
    private void UpdateMetronomeMenu()
    {
        if (metronomeStartStopItem is null) return;
        var following = metronome.Role == MetronomeRole.Follower;
        var text = following
            ? metronome.Muted ? "Unmute metronome" : "Mute metronome"
            : metronome.Playing ? "Stop metronome" : "Start metronome";
        metronomeStartStopItem.Text = "&" + text;
        metronomeStartStopItem.AccessibleName = text;
        metronomeMasterItem.Checked = metronome.Role == MetronomeRole.Master;
        metronomeTimer.Enabled = metronome.NeedsTicks;
    }

    /// <summary>The menu item, the hotkey and the control channel all come here.</summary>
    private void MetronomeStartStop(string from)
    {
        MaybeSayMetronomeHasNoServer();
        metronome.StartStop();
        logFile.Event($"metronome: {(metronome.Role == MetronomeRole.Follower ? (metronome.Muted ? "muted" : "unmuted") : metronome.Playing ? "started" : "stopped")} from {from} - "
            + $"{metronome.Describe()}, {metronomeSettings.Describe()} [{ActiveAudioConfiguration().Describe()}]");
        UpdateMetronomeMenu();
    }

    private void SetMetronomeMaster(bool master)
    {
        // Leading needs a server: it is the server that keeps everyone's beat (Ed, 2026-09-29: "it should not work on p2p").
        if (master && !metronome.OnServer && !MasterWithoutServerForTest)
        {
            logFile.Event("metronome: being the master metronome needs a server - not connected through one");
            if (!NobodyToAnswer)
                ForegroundDialog.Show(owner => AppMessageBox.Show(owner,
                    "Being the master metronome needs a server: the server keeps everyone's beat in time. Connect through a server first.",
                    AppName, MessageBoxButtons.OK, MessageBoxIcon.Information));
            UpdateMetronomeMenu();
            return;
        }
        if (master) metronome.BecomeMaster(Environment.MachineName);
        else metronome.ReleaseMaster();
        LogUiChange("be the master metronome", master ? "on" : "off");
        UpdateMetronomeMenu();
    }

    private void MetronomeTimerTick()
    {
        metronome.Tick();
        if (!metronome.NeedsTicks) metronomeTimer.Enabled = false;
    }

    /// <summary>The per-second tick's part: through a server, tell it when this machine's latest packets were captured; a
    /// master or follower that is no longer connected lets go.</summary>
    private void MetronomeSecondTick()
    {
        UpdateMetronomeRecordDelay();
        ReportCaptureTimesToServer();
        if (connected) return;
        if (metronome.Role == MetronomeRole.Master) { metronome.ReleaseMaster(); UpdateMetronomeMenu(); }
        else if (metronome.Role == MetronomeRole.Follower) metronome.StopFollowing("disconnected", sendLeave: false);
    }

    /// <summary>The server's metronome measures this machine from its own sound: once a second, while connected through a
    /// server, when the latest packets were captured (the server pairs each with when it arrived). Sent whatever the
    /// metronome is doing, so the measurement is ready the moment somebody starts it; one small packet a second.</summary>
    private void ReportCaptureTimesToServer()
    {
        var onServer = metronome.OnServer;
        // Hearing ourselves through the server times each packet's echo by the same capture times (HearMyselfMeter).
        var hearing = relayGroup.HearsItself;
        sender.TimingWanted = onServer || hearing;
        if (!onServer && !hearing) return;
        var reports = sender.TakeTimingReports();
        if (hearing) hearMyselfMeter.NoteSent(reports);
        if (!onServer) return;
        lastReportPackets = reports.Count;
        if (reports.Count > 0 && relayGroup.SendTiming(ServerMetronomeWire.BuildReport(reports))) reportsSent++;
        // Which capture the server times this machine by, and the delay it takes on trust - said when it changes.
        if (sender.TimingSourceForLog is { } source && source != lastTimingSource)
        {
            lastTimingSource = source;
            logFile.Event($"metronome: the server times this computer's sound from {source}");
        }
    }

    private string? lastTimingSource;
    private int lastReportPackets;
    private long reportsSent;
    internal string ReportsForTest => $"wanted {sender.TimingWanted}, {lastReportPackets} packets in the last report, {reportsSent} sent";

    /// <summary>Starting the metronome with no server: nobody else will hear it (Ed, 2026-09-29: "it needs a do not show me
    /// this message again dialogue telling you you're not connected to a server so no one else will be able to hear your
    /// metronome").</summary>
    internal static TaskDialogPage BuildMetronomeNoServerMessage(out TaskDialogVerificationCheckBox dontShowAgain)
    {
        dontShowAgain = new TaskDialogVerificationCheckBox("Do not show me this message again");
        return new TaskDialogPage
        {
            Caption = AppName,
            Heading = "Not connected to a server",
            Text = "You're not connected to a server, so no one else will be able to hear your metronome. It clicks for you alone.",
            Icon = TaskDialogIcon.Information,
            Verification = dontShowAgain,
            Buttons = { TaskDialogButton.OK },
            DefaultButton = TaskDialogButton.OK,
            AllowCancel = true,
        };
    }

    private void MaybeSayMetronomeHasNoServer()
    {
        if (metronome.OnServer || metronome.Role != MetronomeRole.Solo || metronome.Playing) return;   // a master is always on a server
        logFile.Event("metronome: started with no server - it clicks for you alone");
        if (!AppConfig.Load().ShowMetronomeNoServerMessage || NobodyToAnswer) return;
        var page = BuildMetronomeNoServerMessage(out var dontShowAgain);
        ForegroundDialog.Show(owner => AppTaskDialog.ShowDialog(owner, page));
        MetronomeNoServerMessageAnswered(dontShowAgain.Checked);
    }

    internal void MetronomeNoServerMessageAnswered(bool dontShowAgain)
    {
        if (!dontShowAgain) return;
        var cfg = AppConfig.Load();
        cfg.ShowMetronomeNoServerMessage = false;
        try { cfg.Save(); logFile.Event("metronome: the no-server message is hidden from now on (Do not show me this message again)"); }
        catch (Exception ex) { logFile.Event($"metronome: could not save hiding the no-server message: {ex.GetType().Name}: {ex.Message}"); }
    }

    /// <summary>The recording lane's output delay, so a recorded click lands where it was heard against the peers' audio:
    /// its stage's cushion and its output's own delay (the WASAPI figure the latency readout uses; ASIO's stated one).</summary>
    private void UpdateMetronomeRecordDelay()
    {
        var route = receiver.RecordingRoute;
        receiver.Metronome.RecordDelayUs = (receiver.OutputQueueMsFor(route) + receiver.ReportedOutputLatencyMsFor(route)) * 1000;
    }

    /// <summary>Everyone connected, by name, for a master's offer.</summary>
    private IReadOnlyList<(IPEndPoint Endpoint, string Name)> MetronomePeers() =>
        selectedPeerEndpoints.Select(kv => (kv.Value, selectedPeerLabels.GetValueOrDefault(kv.Key, kv.Value.Address.ToString()))).ToList();

    // ---- Settings ----------------------------------------------------------------------------------------------------

    /// <summary>A profile's settings, when it loads and after the window's OK. The sound is loaded here, off the audio path.</summary>
    private void ApplyMetronomeSettings(MetronomeSettings value, string? logAs)
    {
        metronomeSettings = value.Clone();
        // Heard live from the settings window (logAs null), a sound already loaded is used again rather than read from disk
        // on every move of the volume slider; a profile loading, or OK, reads it afresh, so a sound file changed on disk is
        // picked up.
        var sound = logAs is null && metronomeSoundLoaded is { } loaded && loaded.Name.Equals(metronomeSettings.Sound, StringComparison.OrdinalIgnoreCase)
            ? loaded
            : MetronomeSoundLibrary.Load(metronomeSettings.Sound) ?? MetronomeSoundLibrary.Load(MetronomeSoundLibrary.DefaultName);
        metronomeSoundLoaded = sound;
        metronome.Configure(metronomeSettings, sound);
        if (logAs is not null) LogUiChange(logAs, metronomeSettings.Describe());
        UpdateMetronomeMenu();
    }

    /// <summary>The tempo hotkeys and the control channel's tempo up and down (Ed, 2026-09-29): a beat a minute, kept in the
    /// profile as the window's OK keeps it, and heard from the next bar line as any tempo change is. A master's change is
    /// everyone's; following a master, the tempo is theirs and the key changes nothing.</summary>
    private void StepMetronomeTempo(int delta, string from)
    {
        if (metronome.Role == MetronomeRole.Follower)
        {
            logFile.Event($"metronome: tempo {(delta > 0 ? "up" : "down")} from {from} not taken - the tempo is {metronome.MasterName}'s while you follow their master metronome");
            return;
        }
        var value = settings.LoadMetronomeSettings();
        var bpm = Math.Clamp(value.Bpm + delta, MetronomeTimeline.MinBpm, MetronomeTimeline.MaxBpm);
        if (bpm == value.Bpm) return;
        value.Bpm = bpm;
        settings.SaveMetronomeSettings(value);
        MarkProfileDirty();
        ApplyMetronomeSettings(value, $"metronome tempo ({from})");
    }

    /// <summary>The metronome volume hotkeys (Ed, 2026-10-01): 5 points a press, as the received-sound volume keys move, kept
    /// in the profile as the window's OK keeps it. Your own clicks' volume, master or following, so it always counts.</summary>
    private void StepMetronomeVolume(int delta, string from)
    {
        var value = settings.LoadMetronomeSettings();
        var volume = Math.Clamp(value.Volume + delta, 0, 100);
        if (volume == value.Volume) return;
        value.Volume = volume;
        settings.SaveMetronomeSettings(value);
        MarkProfileDirty();
        ApplyMetronomeSettings(value, $"metronome volume ({from})");
    }

    /// <summary>The Metronome settings window as the menu opens it: the profile's settings, the metronome's own Start and Stop
    /// (Mute and Unmute while following), and every change in it heard at once (Ed, 2026-10-02).</summary>
    private MetronomeSettingsDialog BuildMetronomeSettingsDialog(out MetronomeSettings saved)
    {
        saved = settings.LoadMetronomeSettings();
        (string, MetronomeTimeline)? master = metronome.Role == MetronomeRole.Follower ? (metronome.MasterName, metronome.Timeline) : null;
        return new MetronomeSettingsDialog(saved, master, MetronomeLoopbackSentNames(),
            shortcutKeys: hotkeyController.MetronomeKeys, chooseKey: ChooseMetronomeKey,
            metronomeState: () => (metronome.Playing, metronome.Role == MetronomeRole.Follower, metronome.Muted),
            startStop: () => MetronomeStartStop("the metronome settings window"),
            heardLive: s => ApplyMetronomeSettings(s, null));
    }

    /// <summary>Cancel (or Escape): the profile's settings back, heard at once, and a metronome the window started stopped
    /// again - so Cancel still changes nothing.</summary>
    private void CancelMetronomeSettings(MetronomeSettingsDialog dialog, MetronomeSettings saved)
    {
        if (dialog.ChangedLive)
        {
            ApplyMetronomeSettings(saved, null);
            logFile.Event($"metronome: settings window cancelled - the profile's settings heard again ({saved.Describe()}) [{ActiveAudioConfiguration().Describe()}]");
        }
        if (dialog.StartedHere && metronome.Playing && metronome.Role != MetronomeRole.Follower)
            MetronomeStartStop("the metronome settings window's Cancel");
    }

    /// <summary>The usual window for choosing a key, for the Metronome settings window's shortcut list.</summary>
    private static HotkeyInfo? ChooseMetronomeKey(IWin32Window owner)
    {
        using var capture = MainFormHotkeyController.NewHotkeyCaptureForm();
        return capture.ShowDialog(owner) == DialogResult.OK ? capture.CapturedHotkey : null;
    }

    /// <summary>The keys changed in the Metronome settings window: computer-wide, like every shortcut, so kept now and not
    /// with the profile.</summary>
    private void KeepMetronomeKeys(MetronomeSettingsDialog dialog)
    {
        foreach (var i in dialog.ChangedKeys)
        {
            hotkeyController.SetMetronomeKey(i, dialog.ShortcutKeys[i]);
            LogUiChange($"metronome shortcut: {MetronomeSettingsDialog.KeyNames[i]}", dialog.ShortcutKeys[i].ToString());
        }
    }

    private MetronomeSound? metronomeSoundLoaded;

    private void OpenMetronomeSettings()
    {
        using var dialog = BuildMetronomeSettingsDialog(out var saved);
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            CancelMetronomeSettings(dialog, saved);
            return;
        }
        KeepMetronomeKeys(dialog);
        if (!dialog.ChangedSettings) return;
        settings.SaveMetronomeSettings(dialog.Result);
        MarkProfileDirty();
        ApplyMetronomeSettings(dialog.Result, "metronome");
    }

    // ---- Where the clicks play -----------------------------------------------------------------------------------------

    /// <summary>The engine's schedule, to the outputs or the DAW as the settings say, holding the outputs open for it when
    /// nothing else is.</summary>
    private void ApplyMetronomeSchedule(MetronomeSchedule? schedule)
    {
        var inRemSound = metronomeSettings.HearIn == MetronomeHearIn.RemSound;
        UpdateMetronomeLoopbackExclusion();
        receiver.Metronome.Schedule = inRemSound ? schedule : null;
        // Recorded only as heard in RemSound: a DAW records its own tracks.
        receiver.Metronome.Record = inRemSound && metronomeSettings.IncludeInRecordings;
        metronomeDawSchedule = inRemSound ? null : schedule;
        var hold = inRemSound && schedule is not null && metronome.Playing;
        if (hold == receiver.OutputsHeldForMetronome) return;
        if (hold)
        {
            // Outputs as receiving would open them, and open now even with "Receive audio" off.
            ApplyReceiveDevices(starting: true);
            receiver.SetOutputsForMetronome(true);
            logFile.Event(receiver.IsRunning
                ? $"metronome: playing on the outputs receiving uses [{ActiveAudioConfiguration().Describe()}]"
                : $"metronome: outputs opened for the metronome alone (receiving is off) [{ActiveAudioConfiguration().Describe()}]");
        }
        else
        {
            receiver.SetOutputsForMetronome(false);
            if (!receiver.IsRunning) logFile.Event($"metronome: outputs closed again - nothing else uses them [{ActiveAudioConfiguration().Describe()}]");
        }
    }

    /// <summary>The click in the DAW (a track's plugin choosing Metronome), or null.</summary>
    private volatile MetronomeSchedule? metronomeDawSchedule;
    private readonly MetronomeClickLane metronomeDawLane = new("DAW track");

    /// <summary>What a plugin's track reads for each peer it carries, on the plugin link's thread: a peer's audio, or for
    /// the metronome's place in the list its clicks - a whole block, silence between them, placed to be heard when the
    /// plugin plays this block (its lead of blocks from now). Nothing is allocated here.</summary>
    private int ReadForPlugins(IPAddress peer, Span<float> destination, int frames)
    {
        if (!MetronomeDaw.IsPseudoPeer(peer)) return receiver.ReadClaimedPeer(peer, destination, frames);
        var floats = Math.Min(frames * 2, destination.Length);
        destination[..floats].Clear();
        var schedule = metronomeDawSchedule;
        metronomeDawLane.Mix(destination[..floats], 2, PluginBridgeProtocol.WireSampleRate, (schedule?.Clock ?? MetronomeClock.NowUs)(),
            MetronomeDaw.PlayDelayUs(floats / 2), schedule);
        return floats / 2;
    }

    /// <summary>The outputs this profile sends a loopback of - the whole device, or RemSound itself among the applications
    /// sent. Ticked counts, whether "Send my audio" is on right now or not: it can go on at any moment, and the click must
    /// never be in what goes out.</summary>
    private List<(string Id, string Name)> MetronomeLoopbackSent()
    {
        var sent = new List<(string Id, string Name)>();
        var appsMode = RemSound.Sender.ProcessLoopbackCapture.IsSupported && sendModeList.SelectedIndex == SendModeApplicationsIndex;
        if (appsMode)
        {
            // Sending RemSound's own sound sends everything RemSound plays, on every output.
            var self = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
            if (CheckedSendApplicationNames().Any(n => n.Equals(self, StringComparison.OrdinalIgnoreCase)))
                foreach (var c in receiveOutputDevicesList.Items.OfType<AudioDeviceChoice>())
                    if (c.DeviceId is { } id && !c.IsDefaultFollower) sent.Add((id, c.Name));
            return sent;
        }
        foreach (var c in sendOutputDevicesList.CheckedItems.OfType<AudioDeviceChoice>())
        {
            if (c.DeviceId is not { } id) continue;
            if (AudioDefaultFollower.IsLoopbackSend(id))
            {
                if (AudioDefaultFollower.ResolveDefaultRenderId() is { } def) sent.Add((def, "Windows default audio device"));
            }
            else sent.Add((id, c.Name));
        }
        return sent;
    }

    /// <summary>Of the outputs the click would play on, the ones it is kept off, by name.</summary>
    private List<string> MetronomeLoopbackSentNames()
    {
        var outputs = new HashSet<string>(lastAppliedOutputIds ?? [], StringComparer.OrdinalIgnoreCase);
        foreach (var c in receiveOutputDevicesList.CheckedItems.OfType<AudioDeviceChoice>())
            if (c.DeviceId is { } id) outputs.Add(c.IsDefaultFollower ? ResolveDefaultDeviceId(NAudio.CoreAudioApi.DataFlow.Render) ?? id : id);
        return MetronomeLoopbackSent().Where(s => outputs.Contains(s.Id)).Select(s => s.Name).Distinct().ToList();
    }

    /// <summary>Keep the click off every output this profile sends a loopback of, and say so once when that changes.</summary>
    private void UpdateMetronomeLoopbackExclusion()
    {
        var sent = MetronomeLoopbackSent();
        receiver.Metronome.SetLoopbackSent(sent.Select(s => s.Id));
        var names = MetronomeLoopbackSentNames();
        var signature = string.Join("|", names);
        if (signature == metronomeLoopbackSignature) return;
        metronomeLoopbackSignature = signature;
        if (names.Count > 0)
            logFile.Event($"metronome: the click would be sent: it plays on the device you send ({string.Join(", ", names)}) - not played there [{ActiveAudioConfiguration().Describe()}]");
    }

    // ---- The network -------------------------------------------------------------------------------------------------

    private void SendMetronomeMessage(MetronomeMessage message, IPEndPoint to)
    {
        if (currentAudioKey is not { } key) return;   // no password, nothing sealed: the metronome is for password-holders
        var packet = MetronomeWire.BuildPacket(key, message, ++metronomeSequence);
        var went = false;
        try { went = sender.SendVia(packet, packet.Length, to); }
        catch { /* UDP; the next round goes again */ }
        MetronomeSentForTest?.Invoke(message.Kind, to, went);
    }

    /// <summary>A metronome message, on the network thread, stamped the moment it is read. A clock reading is answered
    /// here and now; everything else goes to the window. At most 40 a second from any one address get as far as the key.</summary>
    private void OnMetronomeArrived(byte[] payload, IPEndPoint remote)
    {
        var stamp = metronome.NowUs();
        var second = Environment.TickCount64 / 1000;
        lock (metronomeArrivals)
        {
            var (at, count) = metronomeArrivals.GetValueOrDefault(remote.Address);
            count = at == second ? count + 1 : 1;
            if (metronomeArrivals.Count >= 1024) metronomeArrivals.Clear();
            metronomeArrivals[remote.Address] = (second, count);
            if (count > 40) return;
        }
        if (currentAudioKey is not { } key) return;
        if (!metronomeGuard.TryAccept(key, payload, out var message, out _) || message is null) return;
        if (message.Kind == MetronomeKind.SyncRequest)
        {
            if (metronome.TryAnswerSync(message, remote, stamp) is not { } reply) return;
            var packet = MetronomeWire.BuildPacket(key, reply with { T2 = metronome.NowUs() }, Interlocked.Increment(ref metronomeSequence));
            try { sender.SendVia(packet, packet.Length, remote); }
            catch { /* UDP */ }
            return;
        }
        BeginInvokeIfOpen(() => { if (!IsDisposed) { metronome.OnMessage(message, remote, stamp); UpdateMetronomeMenu(); } });
    }

    /// <summary>Somebody asks to be the master metronome, and this profile asks first. One question at a time, in front of
    /// whatever is on screen; the audio carries on underneath.</summary>
    private void AskAboutMetronomeOffer(MetronomeOffer offer)
    {
        if (IsDisposed) return;
        if (metronomeAskOpen) { metronome.OfferNotAsked(offer); return; }
        if (NobodyToAnswer)
        {
            logFile.Event($"metronome: nobody to ask here - {offer.MasterName} would like to be the master metronome");
            metronome.OfferNotAsked(offer);
            return;
        }
        metronomeAskOpen = true;
        try
        {
            // As the master yourself, it is a handover (Ed, 2026-09-29): asked, never declined for you.
            var question = metronome.Role == MetronomeRole.Master
                ? $"{offer.MasterName} would like to take over as the master metronome.\n\nHand over? Whoever follows you stops, and you follow {offer.MasterName}'s metronome, keeping your own click sound."
                : $"{offer.MasterName} would like to be the master metronome.\n\nFollow their metronome? You keep your own click sound; the tempo and time signature are theirs.";
            var answer = ForegroundDialog.Show(owner => AppMessageBox.Show(owner, question, AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question));
            metronome.AnswerOffer(offer, answer == DialogResult.Yes);
        }
        finally { metronomeAskOpen = false; }
        UpdateMetronomeMenu();
    }

    /// <summary>For the status line and the spoken status: what the metronome is doing, when it is doing anything.</summary>
    private string? MetronomeStatus() => metronome.Role == MetronomeRole.Solo && !metronome.Playing
        ? null
        : "Metronome: " + metronome.Describe();

    // ---- Gate seams --------------------------------------------------------------------------------------------------

    internal MetronomeEngine MetronomeForTest => metronome;
    /// <summary>Gate seam: lead with no server, for the steps that drive the asking and answering directly between two
    /// windows - the same sealed messages a server passes on. Never set outside the gate.</summary>
    internal static bool MasterWithoutServerForTest;
    /// <summary>Gate seam: every metronome message the window sends, where to, and whether the socket took it.</summary>
    internal Action<MetronomeKind, IPEndPoint, bool>? MetronomeSentForTest;
    internal ToolStripMenuItem MetronomeStartStopItemForTest => metronomeStartStopItem;
    internal ToolStripMenuItem MetronomeMasterItemForTest => metronomeMasterItem;
    internal ToolStripMenuItem MetronomeMenuForTest => metronomeMenu;
    internal void MetronomeStartStopForTest(string from) => MetronomeStartStop(from);
    internal void SetMetronomeMasterForTest(bool master) => SetMetronomeMaster(master);
    internal void StepMetronomeTempoForTest(int delta, string from) => StepMetronomeTempo(delta, from);
    internal void StepMetronomeVolumeForTest(int delta, string from) => StepMetronomeVolume(delta, from);
    internal void KeepMetronomeKeysForTest(MetronomeSettingsDialog dialog) => KeepMetronomeKeys(dialog);
    internal MetronomeSettingsDialog BuildMetronomeSettingsDialogForTest(out MetronomeSettings saved) => BuildMetronomeSettingsDialog(out saved);
    internal void CancelMetronomeSettingsForTest(MetronomeSettingsDialog dialog, MetronomeSettings saved) => CancelMetronomeSettings(dialog, saved);
    internal void ApplyMetronomeSettingsForTest(MetronomeSettings value) => ApplyMetronomeSettings(value, "metronome");

    /// <summary>A change to the profile's metronome settings made as the window's OK makes it: kept in the profile, marked
    /// unsaved, applied and logged. The control channel's metronome commands come here.</summary>
    internal void ChangeMetronomeSettingsForTest(Action<MetronomeSettings> change)
    {
        var value = settings.LoadMetronomeSettings();
        change(value);
        settings.SaveMetronomeSettings(value);
        MarkProfileDirty();
        ApplyMetronomeSettings(value, "metronome");
    }
    internal MetronomeSettings MetronomeSettingsForTest => metronomeSettings.Clone();
    internal string? MetronomeStatusForTest => MetronomeStatus();
    internal MetronomeSchedule? MetronomeDawScheduleForTest => metronomeDawSchedule;
    internal MetronomeClickLane MetronomeDawLaneForTest => metronomeDawLane;
    internal void MetronomeArrivedForTest(byte[] payload, IPEndPoint remote) => OnMetronomeArrived(payload, remote);
    internal void MetronomeTickForTest() => metronome.Tick();
    internal List<string> MetronomeLoopbackSentNamesForTest() { UpdateMetronomeLoopbackExclusion(); return MetronomeLoopbackSentNames(); }
}
