using System.Text.RegularExpressions;

namespace RemSound.App;

/// <summary>
/// EVERY WIRED EVENT IN THE PROGRAM IS ACCOUNTED FOR.
///
/// <para>Ed, 2026-08-15: "go through every! single! line! of! code! ... and pin a test to everything
/// associated to every control, every dialogue, every sound, every single bloody event." The control,
/// dialog and cue guards cover the first three. This is the fourth, and it is the one that catches
/// what the others structurally cannot: a handler wired to something that ISN'T a control — a timer, a
/// service callback, a device-change notification, a process-wide event.</para>
///
/// <para><b>Why it works by file rather than by handler.</b> Mapping each of ~280 wirings to a named
/// test would produce a table that is mostly true and impossible to keep honest — the temptation to
/// write a plausible test name next to a handler nobody exercises is exactly the failure this is
/// supposed to prevent. Instead every SOURCE FILE that wires a handler must be claimed: either a
/// suite drives it, or there is a stated reason it needs no test. New file, or an existing file
/// growing its first handler, and the gate stops until somebody has decided which.</para>
///
/// <para><b>It also fails on stale claims.</b> A claim for a file that no longer wires anything is
/// removed, so the registry cannot quietly accumulate reassurance about code that has gone.</para>
///
/// <para>Reads the SOURCE, not the binary. WinForms hides its subscriber lists behind a private
/// EventHandlerList, so reflection can tell you a handler exists but not that you forgot to add one —
/// and "you forgot" is the entire question here.</para>
/// </summary>
internal static partial class SelfTest
{
    /// <summary>How a file's wired handlers are covered. <paramref name="CoveredBy"/> says how, or begins "no test:" with the
    /// reason none is needed. <paramref name="Steps"/> names the gate steps that drive them - the start of each step's name -
    /// and every one must be a step the gate really runs. Until 2026-09-24 the names were free text nothing checked, and
    /// several named suites that never reached the file.</summary>
    private sealed record EventClaim(string File, string CoveredBy, params string[] Steps);

    private static readonly EventClaim[] EventClaims =
    [
        // ---- The main window and its controls ----------------------------------------------------
        // Driven for real by the control suite, in all three audio configurations: every control on
        // the window is enumerated from the live form, so a handler on a new control cannot hide.
        new("RemSound.App/MainForm.cs", "the control suite in all three configurations, with every control specified; a window "
            + "disposed without being closed letting its sound go is the audio-priority guard's, over every step that builds one; the number boxes' Enter key, through the box's own key handling; "
            + "Mute received audio, measured in all three configurations",
            "Control suite - ", "Every control is specified", "GATE GUARD: no step leaves threads holding Windows' audio priority", "JITTER BUFFER BOX",
            "MUTE RECEIVED AUDIO", "WEB LISTENER:"),
        new("RemSound.App/MainForm.NetworkRecording.cs",
            "Record > Save the last minute of the network is driven through the control channel, as a user would: the file written "
            + "to the logs folder and said, and with nothing arrived nothing written",
            "THE NETWORK RECORDER"),
        new("RemSound.App/MainForm.AsioBufferSize.cs",
            "the ASIO buffer size list is driven through the control channel - saved, logged, and the driver reopened once when the "
            + "choice settles - and by the control suite in all three configurations; the timer's own disposal is the ordinary Disposed teardown",
            "ASIO BUFFER SIZE PER PROFILE", "Control suite - "),
        new("RemSound.App/KeyClickService.cs",
            "the key-click output stopping is stood in for by setting what its handler sets, because a test cannot take a "
            + "sound device away; the rebuild that follows is driven through the key-press routine",
            "KEY CLICKS"),
        new("RemSound.App/RelayConnectDialog.cs", "the dialog suite, the relay connect step, and the server window step (moving "
            + "through the remembered servers puts each in the box)",
            "Dialog control suite", "Connecting to a relay, remembering it and ticking who is on it", "SERVER WINDOW",
            "SERVER WINDOW ENTER"),
        new("RemSound.App/ServiceSendHost.cs",
            "Windows saying the network changed, through the routine its handlers call, with the real service loop running; "
            + "the subscription itself is read from source, because a test cannot make Windows' network change",
            "The service looks a name up again until it answers"),
        new("RemSound.App/ServiceNetworkPresence.cs",
            "the relay member list changing, live: the real service host on a stand-in relay whose list says somebody has "
            + "ticked it, and the hello that follows at once ticks them back (since 2026-09-28; it was read from source)",
            "The send-only service, and somebody ticking it on a relay", "The app and the send-only service both take part in relay groups"),
        new("RemSound.App/MainForm.Relay.cs",
            "the control suite drives the tab's controls; the relay step goes in after the address lookup, through the routine "
            + "the Connect button's handler finishes in, and drives the remembered list and the tick list through their handlers",
            "Control suite - ", "Connecting to a relay, remembering it and ticking who is on it"),
        new("RemSound.App/MainFormHotkeyController.cs", "every Alt key, a hotkey change, the Keyboard shortcuts window in the dialog suite, "
            + "the metronome's keys' actions, the unmute-myself key's and the mute-received key's",
            "Keyboard: every Alt key goes where it says", "AUDIT: changing a hotkey does not claim", "Dialog control suite", "METRONOME MENU", "METRONOME TEMPO KEYS",
            "METRONOME VOLUME KEYS", "HEAR MYSELF BUTTON", "MUTE RECEIVED AUDIO"),
        new("RemSound.App/MainFormTrayController.cs",
            "the tray icon's right press and the menu closing are driven; the menu items call the same toggles, restore and exit "
            + "the control suite drives",
            "HEADLESS: the tray menu still opens"),

        // ---- Dialogs. The dialog suite enumerates every window in the app and audits every
        // interactive control in each, so these are covered by construction rather than by listing. --
        new("RemSound.App/AboutDialog.cs", "the dialog suite", "Dialog control suite"),
        new("RemSound.App/AddBandDialog.cs", "the dialog suite", "Dialog control suite"),
        new("RemSound.App/ManualPeerPrompt.cs", "the dialog suite", "Dialog control suite"),
        new("RemSound.App/PreferencesDialog.cs", "the dialog suite, the sounds, muting every cue, remembering the main window, and opening the current log and the logs folder (driven through the control channel)", "Dialog control suite", "Every sound is pinned", "MUTE ALL AUDIO CUES:", "MAIN WINDOW PLACE:", "Logging: Preferences opens the current log"),
        new("RemSound.App/ProfilePasswordDialog.cs", "the dialog suite", "Dialog control suite"),
        new("RemSound.App/ProfileSaveAsPrompt.cs", "the dialog suite", "Dialog control suite"),
        new("RemSound.App/ProfileSelectionDialog.cs", "the dialog suite", "Dialog control suite"),
        new("RemSound.App/QuickProfileSwitchDialog.cs", "the dialog suite", "Dialog control suite"),
        new("RemSound.App/RecordingSettingsDialog.cs", "the dialog suite", "Dialog control suite"),
        new("RemSound.App/PushToTalkDialog.cs",
            "every control of the window driven through the control channel, and OK and Cancel, as a person uses them; and the dialog suite",
            "PUSH TO TALK WINDOW", "PUSH TO TALK DOUBLE TAP PREVIEW", "Dialog control suite"),
        new("RemSound.App/MainForm.PushToTalk.cs", "the button after Send my audio, clicked through the control channel to open the window",
            "PUSH TO TALK WINDOW"),
        new("RemSound.App/MainForm.HearMyself.cs", "the Audio profile button, clicked through the control channel to unmute and mute",
            "HEAR MYSELF BUTTON"),
        new("RemSound.App/MainForm.ServerVersion.cs", "the out-of-date server warning's Visit GitHub button, clicked: the newest release's page and the log",
            "OUT-OF-DATE SERVERS"),
        new("RemSound.App/MetronomeSettingsDialog.cs",
            "every control of the window driven through the control channel, and OK and Cancel, as a person uses them; the sound "
            + "list's preview; the shortcut list's Del through its own key handler, and its Enter and double-click's one routine; "
            + "the Listen button, pressed through the control channel, and the watch that keeps its words in step (its routine, "
            + "driven after a start from the menu); and the dialog suite",
            "METRONOME WINDOW", "Dialog control suite", "METRONOME VOLUME KEYS", "METRONOME LISTEN"),
        new("RemSound.App/MainForm.Metronome.cs",
            "the Metronome menu's three items chosen through the control channel's menu command, and the engine's timer ticking in "
            + "the two-window master test",
            "METRONOME MENU", "METRONOME MASTER"),
        new("RemSound.App/RenamePeerDialog.cs", "the dialog suite", "Dialog control suite"),
        new("RemSound.App/MainForm.NameLookups.cs", "the network-change handler, invoked as Windows invokes it, looks the waiting names up and connects them",
            "NAMES: a server or peer not found at start"),
        new("RemSound.App/PluginInstallPlaceDialog.cs", "the dialog suite, and each button pressed through the real menu - the standard place, "
            + "another folder through Windows' picker, Cancel", "Dialog control suite", "PLUGIN FOLDER: the menu asks where"),
        new("RemSound.Plugin/PluginHelpSounds.cs", "no test: its one handler disposes the device and the file once a sound ends, as "
            + "CuePlayer's does; the gate opens no device, and the plays themselves are counted by the plugin's sound step"),
        new("RemSound.App/ServiceProfileDialog.cs", "the dialog suite, and its additional options' Cancel and OK",
            "Dialog control suite", "Keyboard: Additional service options can be cancelled"),
        new("RemSound.App/UpdateInstallNoticeDialog.cs", "the dialog suite", "Dialog control suite"),
        new("RemSound.App/AsioLoadingSplash.cs",
            "no test: its one handler tells the loading thread the splash is on screen. The splash is text only, with nothing "
            + "to drive, and it is not a dialog the audits reach (see 'Every window is reachable')"),

        // ---- Shared accessible controls ------------------------------------------------------------
        new("RemSound.App/CheckedListAccessibility.cs", "every checked list on the window, driven by the control suite", "Control suite - "),
        new("RemSound.Ui/FormLayoutRows.cs", "the labels of every dialog built with it, in the dialog suite and the Alt-key check",
            "Dialog control suite", "Keyboard: every Alt key goes where it says"),
        new("RemSound.Ui/PluginEditorPanel.cs", "the plugin window, its F1 and its Context help button",
            "Plugin window (peer list, job, bypass", "Context help: F1 on every control in every window"),
        new("RemSound.Ui/ContextHelpWindow.cs", "the context help window: its manual button pressed once in every window",
            "Context help: F1 on every control in every window"),
        new("RemSound.Ui/ListSelection.cs", "Ctrl+A in a list that holds several, and Manage named peers' Delete of them all",
            "LISTS: Ctrl+A selects every row"),
        new("RemSound.Core/HotkeyCaptureForm.cs", "the dialog suite and the Alt-key check (the \"press a key\" window)",
            "Dialog control suite", "Keyboard: every Alt key goes where it says"),

        // ---- The plugin -----------------------------------------------------------------------------
        new("RemSound.Plugin/RemSoundPlugin.cs", "the plugin's parameters, and both directions through one instance",
            "Plugin parameters (", "Plugin: one instance sending AND receiving"),
        new("RemSound.Core/PluginBridgeClient.cs", "the link itself, and the link inside the real window",
            "App-plugin link: framing", "PLUGIN LINK IN THE APP"),
        new("RemSound.Core/PluginBridgeHost.cs", "the link itself, and the link inside the real window",
            "App-plugin link: framing", "PLUGIN LINK IN THE APP"),

        // ---- Engine and services ---------------------------------------------------------------------
        new("RemSound.Core/PeerDiscoveryService.cs", "a malformed broadcast, a remembered peer following discovery, and expiry",
            "AUDIT DISC1", "A remembered peer the DNS got wrong still follows discovery", "Somebody who goes away leaves the list"),
        new("RemSound.Receiver/MultiOutputPlayout.cs",
            "what the lost-output handler marks is read by the per-second heal, driven with the fault forced (the handler itself fires "
            + "only when a real output dies, and the gate opens none - see the plan's note on a stand-in output stage), and the churn starts and stops outputs",
            "AUDIT: the faulted-output heal actually runs in the per-second tick", "Lifecycle churn"),
        new("RemSound.Core/SharedAsioDevice.cs",
            "the churn (its ASIO leg only with REMSOUND_TEST_ASIO), and the real-driver duplex step when REMSOUND_ASIO_TEST_DRIVER names one",
            "Lifecycle churn", "ASIO: one driver instance carries both directions"),
        new("RemSound.Sender/AudioSessionStartWatcher.cs", "the watcher's life, catching an application as it opens, and the service waiting for one",
            "Session-start watcher lifecycle", "Send-app capture change-detection", "AUDIT: the service sends a chosen application"),
        new("RemSound.Sender/CaptureSource.cs", "two sources aligned, a mix that never waits, drift in the mixer's units, a dead device event",
            "AUDIT: two capture sources in one stream stay aligned", "AUDIT: adding a capture source cannot freeze sending",
            "AUDIT: a 44.1 kHz mono 16-bit capture source", "AUDIT: capture survives a sound card whose event never fires"),
        new("RemSound.Sender/PushModeWasapiBackend.cs",
            "its two handlers are the capture device's own callbacks and need a real device; the churn opens one when there is "
            + "one, and the sample-format rule they feed is checked on its own",
            "Lifecycle churn", "AUDIT: tight-latency capture reads integer PCM"),
        new("RemSound.App/RecordingController.cs", "multi-peer split, every mode by content, and the dying-writer report",
            "Recording: two peers", "Recording: every source and channel mode", "Recording reports its own death"),
        new("RemSound.App/CuePlayer.cs", "every sound", "Every sound is pinned"),
        new("RemSound.App/WebListen/WebListener.cs", "the web listener (a frame counter, not an event)", "WEB LISTENER:"),
        new("RemSound.App/WebListen/TailscaleFunnel.cs", "no test: its two handlers collect Tailscale's own command-line output; "
            + "the gate never runs Tailscale (the Web listener window is checked with pretend answers), and how Funnel's answer is read is checked by WEB LISTENER"),
        new("RemSound.App/WebListen/WebListenerDialog.cs", "the Web listener window: the tick box, the password set on leaving the box, the start-by-itself tick box (in force at once, put back by Cancel, saved by OK), the address, Copy address and Turn on Tailscale Funnel (with a pretend Tailscale), OK, Cancel and the status, driven through the control channel", "WEB LISTENER:", "Dialog control suite"),
        new("RemSound.App/AppMessageBox.cs", "the silent question window's buttons, pressed through the channel for a message box and a task dialog",
            "REMOTE CONTROL: the whole app is driven through the channel"),
        new("RemSound.App/CommandLineActions.cs",
            "no test: its one handler closes a command's log file as the process ends; driving it means ending the process, "
            + "and what reaches that file first is checked by the scripts step"),
        new("RemSound.App/PowerResumeHandler.cs",
            "no test: it only forwards Windows' resume notification. The wake steps call the path it forwards to directly, " +
            "because a gate run cannot put the machine to sleep"),

        // ---- Deliberately untested, with the reason ---------------------------------------------------
        new("RemSound.App/Program.cs",
            "no test: process-wide last-resort handlers (unhandled exception, unobserved task, application exit), which mean " +
            "crashing the test process to drive - what they do is checked by the crash-report cap test - and the single-" +
            "instance hand-offs (a second launch asking this one to show itself, or --close), whose coordinator is driven " +
            "by the --close step, with this wiring read from source there",
            "AUDIT: --close asks the running copy to close before ending it"),
        new("RemSound.App/AppInstaller.cs",
            "no test: fires while an elevated installer copies files over the running app. Exercising it for real " +
            "would install RemSound on the machine running the gate"),
        new("RemSound.App/CommandLine.cs",
            "no test: the standalone plugin window's job callback, which does nothing by design — that window " +
            "carries no audio, and the status text says so"),
        new("RemSound.App/RouterPortMapper.cs",
            "a stand-in router found, mapped, asked again on the beat, refusing and turned off; how a real router handles "
            + "the request is not provable here",
            "The router mapping is asked for again before it runs out"),
    ];

    /// <summary>How many handlers each claimed file wired when its claim was last read against them (2026-09-28). A claim is
    /// per FILE, so a new handler in a file already claimed - MainForm.cs holds most of them - passed unreviewed. Now a count
    /// that moves fails until somebody reads the claim again and sets the new number here.</summary>
    private static readonly Dictionary<string, int> ReviewedHandlerCounts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["RemSound.App/AboutDialog.cs"] = 1,
        ["RemSound.App/AddBandDialog.cs"] = 4,
        ["RemSound.App/AppInstaller.cs"] = 1,
        ["RemSound.App/AppMessageBox.cs"] = 1,
        ["RemSound.App/AsioLoadingSplash.cs"] = 1,
        ["RemSound.App/CheckedListAccessibility.cs"] = 6,
        ["RemSound.App/CommandLine.cs"] = 1,
        ["RemSound.App/CommandLineActions.cs"] = 1,
        ["RemSound.App/CuePlayer.cs"] = 1,
        ["RemSound.App/KeyClickService.cs"] = 1,
        ["RemSound.App/MainForm.cs"] = 127,
        ["RemSound.App/MainForm.AsioBufferSize.cs"] = 2,
        ["RemSound.App/MainForm.NameLookups.cs"] = 2,
        ["RemSound.App/MainForm.NetworkRecording.cs"] = 1,
        ["RemSound.App/MainForm.Relay.cs"] = 2,
        ["RemSound.App/MainFormHotkeyController.cs"] = 27,
        ["RemSound.App/MainFormTrayController.cs"] = 11,
        ["RemSound.App/ManualPeerPrompt.cs"] = 1,
        ["RemSound.App/PluginInstallPlaceDialog.cs"] = 2,
        ["RemSound.App/PowerResumeHandler.cs"] = 1,
        ["RemSound.App/PreferencesDialog.cs"] = 53,
        ["RemSound.App/WebListen/WebListener.cs"] = 1,
        ["RemSound.App/WebListen/TailscaleFunnel.cs"] = 2,
        ["RemSound.App/WebListen/WebListenerDialog.cs"] = 11,
        ["RemSound.App/ProfilePasswordDialog.cs"] = 2,
        ["RemSound.App/ProfileSaveAsPrompt.cs"] = 1,
        ["RemSound.App/ProfileSelectionDialog.cs"] = 6,
        ["RemSound.App/Program.cs"] = 6,
        ["RemSound.App/QuickProfileSwitchDialog.cs"] = 5,
        ["RemSound.App/RecordingController.cs"] = 1,
        ["RemSound.App/RecordingSettingsDialog.cs"] = 8,
        ["RemSound.App/PushToTalkDialog.cs"] = 11,
        ["RemSound.App/MainForm.PushToTalk.cs"] = 1,
        ["RemSound.App/MainForm.HearMyself.cs"] = 1,
        ["RemSound.App/MetronomeSettingsDialog.cs"] = 12,
        ["RemSound.App/MainForm.ServerVersion.cs"] = 1,
        ["RemSound.App/MainForm.Metronome.cs"] = 4,
        ["RemSound.App/RelayConnectDialog.cs"] = 9,
        ["RemSound.App/RenamePeerDialog.cs"] = 2,
        ["RemSound.App/RouterPortMapper.cs"] = 1,
        ["RemSound.App/ServiceNetworkPresence.cs"] = 1,
        ["RemSound.App/ServiceProfileDialog.cs"] = 11,
        ["RemSound.App/ServiceSendHost.cs"] = 2,
        ["RemSound.App/UpdateInstallNoticeDialog.cs"] = 4,
        ["RemSound.Core/HotkeyCaptureForm.cs"] = 3,
        ["RemSound.Core/PeerDiscoveryService.cs"] = 1,
        ["RemSound.Core/PluginBridgeClient.cs"] = 1,
        ["RemSound.Core/PluginBridgeHost.cs"] = 3,
        ["RemSound.Core/SharedAsioDevice.cs"] = 1,
        ["RemSound.Plugin/PluginHelpSounds.cs"] = 1,
        ["RemSound.Plugin/RemSoundPlugin.cs"] = 10,
        ["RemSound.Receiver/MultiOutputPlayout.cs"] = 1,
        ["RemSound.Sender/AudioSessionStartWatcher.cs"] = 1,
        ["RemSound.Sender/CaptureSource.cs"] = 2,
        ["RemSound.Sender/PushModeWasapiBackend.cs"] = 2,
        ["RemSound.Ui/ContextHelpWindow.cs"] = 1,
        ["RemSound.Ui/FormLayoutRows.cs"] = 3,
        ["RemSound.Ui/ListSelection.cs"] = 1,
        ["RemSound.Ui/PluginEditorPanel.cs"] = 12,
    };

    private static string? EveryWiredEventIsClaimed()
    {
        var root = FindSourceRoot();
        if (root is null)
            return Skip("the source tree is not reachable from here (set REMSOUND_SOURCE_ROOT, as run-tests.ps1 does)");

        var srcDir = Path.Combine(root, "src");
        var wiring = HandlerWiringPattern();
        var found = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var totalHandlers = 0;

        foreach (var file in Directory.GetFiles(srcDir, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(srcDir, file).Replace('\\', '/');
            // Skip build output, and skip the gate's own files: a test wiring a handler in order to
            // observe something is not app behaviour that needs covering.
            if (relative.Contains("/obj/") || relative.Contains("/bin/")) continue;
            if (Path.GetFileName(file).StartsWith("SelfTest", StringComparison.OrdinalIgnoreCase)) continue;

            var count = wiring.Matches(File.ReadAllText(file)).Count(m => !NotEvents.Contains(m.Groups[1].Value));
            if (count == 0) continue;
            found[relative] = count;
            totalHandlers += count;
        }

        Check(found.Count > 0, "the scan found no wired handlers at all — it has stopped working, which would make this guard silently useless");

        var claims = EventClaims.ToDictionary(c => c.File, c => c.CoveredBy, StringComparer.OrdinalIgnoreCase);

        var unclaimed = found.Keys.Where(f => !claims.ContainsKey(f)).OrderBy(f => f).ToList();
        Check(unclaimed.Count == 0,
            $"these files wire event handlers that nothing has claimed — say which suite drives them, or state why none is needed: {string.Join(", ", unclaimed)}");

        var stale = claims.Keys.Where(f => !found.ContainsKey(f)).OrderBy(f => f).ToList();
        Check(stale.Count == 0,
            $"these claims are stale — the file no longer wires anything, so the claim is reassurance about code that has gone: {string.Join(", ", stale)}");

        // THE STEPS A CLAIM NAMES MUST EXIST. Read from the gate's own source - the literal start of each step's name, up
        // to any part filled in as it runs - so a claim naming a step that was renamed or never existed fails here.
        var stepNames = new List<string>();
        foreach (var file in Directory.GetFiles(Path.Combine(srcDir, "RemSound.App"), "SelfTest*.cs"))
            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"RunStep\(results, \$?""([^""{]*)"))
                stepNames.Add(m.Groups[1].Value);
        Check(stepNames.Count > 100, $"the step names must be readable from the gate's source ({stepNames.Count} found)");
        var covered = EventClaims.Where(c => !c.CoveredBy.StartsWith("no test", StringComparison.OrdinalIgnoreCase)).ToList();
        var nameless = covered.Where(c => c.Steps.Length == 0).Select(c => c.File).ToList();
        Check(nameless.Count == 0, $"a claim that its handlers are driven must name the steps that drive them: {string.Join(", ", nameless)}");
        var missingSteps = EventClaims.SelectMany(c => c.Steps.Where(s => !stepNames.Any(n => n.StartsWith(s, StringComparison.Ordinal)))
                                                   .Select(s => $"{c.File}: \"{s}\""))
            .ToList();
        Check(missingSteps.Count == 0, $"these claims name steps the gate does not run: {string.Join("; ", missingSteps)}");

        // A reason is required, not a shrug. "no test" on its own tells the next person nothing.
        var bare = EventClaims.Where(c => c.CoveredBy.StartsWith("no test", StringComparison.OrdinalIgnoreCase) && c.CoveredBy.Length < 40)
            .Select(c => c.File).ToList();
        Check(bare.Count == 0, $"an exemption must carry its reason, not just the word 'no test': {string.Join(", ", bare)}");

        var moved = found.Where(kv => ReviewedHandlerCounts.GetValueOrDefault(kv.Key, -1) != kv.Value)
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => $"{kv.Key}: {ReviewedHandlerCounts.GetValueOrDefault(kv.Key)} -> {kv.Value}").ToList();
        Check(moved.Count == 0,
            $"these files wire a different number of handlers from when their claims were last read: {string.Join("; ", moved)}. "
            + "Read each claim again against the new handlers, then set the number in ReviewedHandlerCounts");
        var gone = ReviewedHandlerCounts.Keys.Where(f => !found.ContainsKey(f)).ToList();
        Check(gone.Count == 0, $"these counted files wire nothing now - take them off ReviewedHandlerCounts: {string.Join(", ", gone)}");

        var exempt = EventClaims.Count(c => c.CoveredBy.StartsWith("no test", StringComparison.OrdinalIgnoreCase));
        var exemptHandlers = EventClaims.Where(c => c.CoveredBy.StartsWith("no test", StringComparison.OrdinalIgnoreCase))
            .Sum(c => found.GetValueOrDefault(c.File));

        return $"{totalHandlers} wired handlers across {found.Count} files, every one claimed: "
             + $"{totalHandlers - exemptHandlers} driven by a named suite, {exemptHandlers} in {exempt} files exempt with a stated reason";
    }

    /// <summary>Where the source lives. The gate normally runs from a throwaway publish folder with no
    /// source anywhere near it, so run-tests.ps1 hands the path over; walking up is the fallback for
    /// running the built exe straight out of bin.</summary>
    private static string? FindSourceRoot()
    {
        var supplied = Environment.GetEnvironmentVariable("REMSOUND_SOURCE_ROOT");
        if (!string.IsNullOrWhiteSpace(supplied) && Directory.Exists(Path.Combine(supplied, "src", "RemSound.App")))
            return supplied;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "RemSound.App"))) return dir.FullName;
        }
        return null;
    }

    /// <summary>Matches a handler being wired: <c>something.SomeEvent += ...</c>.
    ///
    /// <para>Anchored to a STATEMENT boundary — start of line, or after <c>{</c>, <c>;</c>, <c>}</c> or
    /// <c>=&gt;</c> — not merely to the start of a line. Anchoring to the line start alone silently
    /// missed <c>try { NetworkChange.NetworkAddressChanged += … }</c>, and a scan that misses wirings
    /// makes this whole guard worse than useless: it would report full coverage over a gap.</para>
    ///
    /// <para>Requires a dotted member, so plain arithmetic (<c>total += n</c>) never matches. A
    /// property being appended to (<c>label.Text += …</c>) still would, so those names are filtered out
    /// by <see cref="NotEvents"/>. The residual risk is only ever an over-count inside a file that is
    /// already claimed — never a real handler slipping through, which is the direction that matters.</para></summary>
    [GeneratedRegex(@"(?:^|[{;}]|=>)[ \t]*[A-Za-z_][A-Za-z0-9_\.\(\)\[\]?!]*\.([A-Za-z_][A-Za-z0-9_]*) \+= ", RegexOptions.Multiline)]
    private static partial Regex HandlerWiringPattern();

    /// <summary>Members that are appended to rather than subscribed to. Anything here is not a handler.</summary>
    private static readonly HashSet<string> NotEvents = new(StringComparer.Ordinal) { "Text" };
}
