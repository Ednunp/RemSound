using System.Net;
using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// HEAR MYSELF THROUGH THE SERVER (Ed, 2026-10-01: "rather than showing a mute button for each peer and including it in the
/// peers list, we put a button in the audio profile tab, unmute myself to test round trip. then mute myself when it's
/// unmuted"). The server sends this computer's own sound back, as Jamulus sends you your own channel, and it plays through
/// your own receive buffer and outputs like anyone else's - so what you hear is the whole round trip. The first time, a
/// warning about loopback (Ed: "make sure 1st time you use it it has a do not show this message again dialogue about not
/// using loop back"). Off by default, never saved with a profile, and off again on leaving the server. The server's part
/// is RelayGroupClient.SetHearMyself and server-v2.15; PLAN-hear-myself-2026-10-01.md holds the decisions.
/// </summary>
public sealed partial class MainForm
{
    // Ed, 2026-10-01: "rename it to unmute myself to test round trip on server. it only works on a server after all."
    private const string UnmuteMyselfText = "Unmute my&self to test round trip on server (Alt+S)";
    private const string UnmuteMyselfName = "Unmute myself to test round trip on server";
    private const string MuteMyselfText = "Mute my&self (Alt+S)";
    private const string MuteMyselfName = "Mute myself";

    /// <summary>How long with nothing of ours coming back - from the start, or since the last that did - before we are muted
    /// again: an older server that does not send it, or one that stopped.</summary>
    internal static readonly TimeSpan HearMyselfNothingBackAfter = TimeSpan.FromSeconds(5);

    private readonly Button hearMyselfButton = new() { Text = UnmuteMyselfText, AccessibleName = UnmuteMyselfName, AutoSize = true, Visible = false };
    private readonly HearMyselfMeter hearMyselfMeter = new();
    private DateTime? hearMyselfSinceUtc;
    private DateTime? hearMyselfLastBackUtc;

    private void WireHearMyselfButton()
    {
        hearMyselfButton.Click += (_, _) => ToggleHearMyself("the button");
        UpdateHearMyselfButton();
    }

    /// <summary>The button says what pressing it does now, and is there only while you are on a server (Ed, 2026-10-01: "the
    /// button should vanish if you're not connected to a server"). Called on connecting and leaving too
    /// (UpdateRelayControls).</summary>
    private void UpdateHearMyselfButton()
    {
        var on = relayGroup.HearsItself;
        hearMyselfButton.Text = on ? MuteMyselfText : UnmuteMyselfText;
        hearMyselfButton.AccessibleName = on ? MuteMyselfName : UnmuteMyselfName;
        hearMyselfButton.Visible = on || relayGroup.ConnectedRelay is not null;
    }

    /// <summary>The button, or its global hotkey (Ed, 2026-10-01): the same either way, the warning and all.</summary>
    private void ToggleHearMyself(string from)
    {
        if (relayGroup.HearsItself) StopHearingMyself($"you muted yourself ({from})");
        else StartHearingMyself(from);
    }

    /// <summary>Why your own sound cannot come back now, or null when it can: it needs a server, something being sent to come
    /// back, and Receive audio on to hear it.</summary>
    internal static string? WhyCannotHearMyself(bool onServer, bool sending, bool playing)
    {
        if (!onServer) return "You're not on a server. Connect to one first (Connect to server, on the Connectivity tab), then unmute yourself to hear your round trip.";
        if (!sending) return "Nothing is being sent, so there is nothing to come back. Turn on Send my audio, with an input ticked, on the Audio inputs and outputs tab first.";
        if (!playing) return "Receive audio is off, so you wouldn't hear yourself. Turn on Receive audio, on the Audio inputs and outputs tab, first.";
        return null;
    }

    private void StartHearingMyself(string from)
    {
        var (sending, receiving) = HearMyselfPrereqsForTest ?? (sender.IsRunning || pluginTrackSource.AnyHostSending, IsReceiveEnabled);
        var why = WhyCannotHearMyself(relayGroup.ConnectedRelay is not null, sending, receiving);
        if (why is not null)
        {
            logFile.Event($"hear myself: not unmuted - {why} [{ActiveAudioConfiguration().Describe()}]");
            if (!NobodyToAnswer)
                ForegroundDialog.Show(owner => AppMessageBox.Show(owner, why, AppName, MessageBoxButtons.OK, MessageBoxIcon.Information));
            return;
        }
        if (!ConfirmHearMyselfWarning()) return;
        if (!relayGroup.SetHearMyself(true)) return;
        hearMyselfMeter.Reset();
        hearMyselfSinceUtc = DateTime.UtcNow;
        hearMyselfLastBackUtc = null;
        AfterHearMyselfChanged();
        LogUiChange("unmute myself to test round trip", $"on ({from})");
    }

    /// <summary>Mute yourself again: the server stops sending your sound back. Also on leaving the server, and when nothing comes
    /// back from it.</summary>
    private void StopHearingMyself(string why)
    {
        if (!relayGroup.HearsItself) { UpdateHearMyselfButton(); return; }
        relayGroup.SetHearMyself(false);
        AfterHearMyselfChanged();
        logFile.Event($"hear myself: muted - {why} [{ActiveAudioConfiguration().Describe()}]");
    }

    /// <summary>Who our sound goes to and whose we let in both change with it; then the button.</summary>
    private void AfterHearMyselfChanged()
    {
        if (!relayGroup.HearsItself)
        {
            hearMyselfSinceUtc = null;
            hearMyselfMeter.Reset();
        }
        PushAllowedReceiveSenders();
        ApplyAudioRuntime();
        UpdateHearMyselfButton();
    }

    /// <summary>Leaving (or changing) the server mutes you: your own sound was on the server you left. The relay group has
    /// already stopped asking for it; this puts the rest of the window in step.</summary>
    private void HearMyselfLeftServer()
    {
        if (hearMyselfSinceUtc is null) { UpdateHearMyselfButton(); return; }
        hearMyselfSinceUtc = null;
        hearMyselfMeter.Reset();
        UpdateHearMyselfButton();
        logFile.Event($"hear myself: muted - you left the server [{ActiveAudioConfiguration().Describe()}]");
    }

    /// <summary>The first time: what your own sound coming back can do with a loopback, until hidden. False if cancelled.</summary>
    private bool ConfirmHearMyselfWarning()
    {
        if (!AppConfig.Load().ShowHearMyselfWarning || NobodyToAnswer) return true;
        var page = BuildHearMyselfWarning(out var dontShowAgain);
        var answer = ForegroundDialog.Show(owner => AppTaskDialog.ShowDialog(owner, page));
        var go = answer != TaskDialogButton.Cancel;
        HearMyselfWarningAnswered(go, dontShowAgain.Checked);
        return go;
    }

    /// <summary>Ed's warning, not shown: headphones, and no loopback of what you hear while your own sound comes back.</summary>
    internal static TaskDialogPage BuildHearMyselfWarning(out TaskDialogVerificationCheckBox dontShowAgain)
    {
        dontShowAgain = new TaskDialogVerificationCheckBox("Do not show me this message again");
        return new TaskDialogPage
        {
            Caption = AppName,
            Heading = "Before you hear yourself back",
            Text = "The server will send your own sound back to you, so you hear the whole round trip. Use headphones.\n\n"
                 + "Don't send a loopback of what you hear while it's on, such as your interface's loopback channels or an app "
                 + "capturing RemSound's output. Your sound would go round and round and build into feedback.",
            Icon = TaskDialogIcon.Warning,
            Verification = dontShowAgain,
            Buttons = { TaskDialogButton.OK, TaskDialogButton.Cancel },
            DefaultButton = TaskDialogButton.OK,
            AllowCancel = true,
        };
    }

    /// <summary>What closing the warning does: OK goes on, Cancel stays muted; with the tick, it is not shown again.</summary>
    internal void HearMyselfWarningAnswered(bool wentOn, bool dontShowAgain)
    {
        logFile.Event($"hear myself: the loopback warning was answered {(wentOn ? "OK" : "Cancel")}");
        if (!dontShowAgain) return;
        var cfg = AppConfig.Load();
        cfg.ShowHearMyselfWarning = false;
        try { cfg.Save(); logFile.Event("hear myself: the loopback warning is hidden from now on (Do not show me this message again)"); }
        catch (Exception ex) { logFile.Event($"hear myself: could not save hiding the loopback warning: {ex.GetType().Name}: {ex.Message}"); }
    }

    /// <summary>While you hear yourself, the server you are on stays one of our sound's targets however it answers the
    /// once-a-second check. With nobody paired there it never answers it, and after 30 s the usual rule
    /// (<see cref="EndpointsToArm"/>) took it for unreachable and stopped sending - your own sound stopped coming back 12
    /// seconds after Ed first unmuted (2026-10-01, 15:56:30). What keeps this from sending into the void instead is
    /// <see cref="TickHearMyself"/>: nothing back for <see cref="HearMyselfNothingBackAfter"/> mutes you.</summary>
    internal static bool HearMyselfKeepsArmed(IPAddress address, bool hearsItself, IPEndPoint? server) =>
        hearsItself && server is not null && server.Address.Equals(address);

    /// <summary>Once a second while unmuted: how long our sound takes to come back, and to be heard. With nothing back for
    /// <see cref="HearMyselfNothingBackAfter"/> - from the start (an older server, which does not send it) or since the last
    /// that came (the server, or our sending, stopped) - say so and mute.</summary>
    private void TickHearMyself()
    {
        if (hearMyselfSinceUtc is not { } since || !relayGroup.HearsItself) return;
        var (arrivals, timed, typicalMs, longestMs) = hearMyselfMeter.TakeSecond();
        var now = DateTime.UtcNow;
        if (arrivals > 0) hearMyselfLastBackUtc = now;
        if (now - (hearMyselfLastBackUtc ?? since) >= HearMyselfNothingBackAfter)
        {
            var never = hearMyselfLastBackUtc is null;
            var text = never
                ? "Nothing has come back from the server. It may be an older one that doesn't send your own sound back, so you've been muted again."
                : "Your sound has stopped coming back from the server, so you've been muted again.";
            StopHearingMyself(never
                ? $"nothing came back from the server in {HearMyselfNothingBackAfter.TotalSeconds:0} s (an older server?)"
                : $"your sound stopped coming back from the server for {HearMyselfNothingBackAfter.TotalSeconds:0} s");
            if (!NobodyToAnswer)
                ForegroundDialog.Show(owner => AppMessageBox.Show(owner, text, AppName, MessageBoxButtons.OK, MessageBoxIcon.Information));
            return;
        }
        if (relayGroup.OwnEchoAddress is not { } ownEcho || typicalMs is not { } typical) return;
        var lanes = receiver.StreamDepthsFrom(ownEcho)
            .Select(s =>
            {
                var outputMs = receiver.OutputQueueMsFor(s.Route) + receiver.ReportedOutputLatencyMsFor(s.Route);
                return $"{s.Lane} stream {s.StreamId}, playing on {s.Route}: this computer's buffer {s.BufferedMs} ms and output {outputMs:0.0} ms, "
                     + $"about {typical + s.BufferedMs + outputMs:0} ms from playing to hearing yourself";
            });
        logFile.Event($"hear myself [{ActiveAudioConfiguration().Describe()}]: back from the server {typical:0.0} ms after the input "
                    + $"(typical of {timed} packets timed, longest {longestMs:0.0} ms, {arrivals} back this second) | {string.Join(" | ", lanes)}");
    }

    /// <summary>Test seam: whether something is being sent and Receive audio is on, for a window with no devices open.</summary>
    internal (bool Sending, bool Receiving)? HearMyselfPrereqsForTest;
    internal Button HearMyselfButtonForTest => hearMyselfButton;
    internal Control MeasuredLatencyReadoutForTest => measuredLatencyReadout;
    internal void ToggleHearMyselfForTest() => ToggleHearMyself("the button");
    internal void TickHearMyselfForTest() => TickHearMyself();
    internal HearMyselfMeter HearMyselfMeterForTest => hearMyselfMeter;
    internal void BackdateHearMyselfForTest(TimeSpan by)
    {
        if (hearMyselfSinceUtc is { } at) hearMyselfSinceUtc = at - by;
        if (hearMyselfLastBackUtc is { } back) hearMyselfLastBackUtc = back - by;
    }
    internal IPEndPoint[] SendTargetsNowForTest() => SelectedSendEndpoints();
}
