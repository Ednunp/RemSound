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
        new("RemSound.App/MainForm.cs", "the control suite in all three configurations, with every control specified",
            "Control suite - ", "Every control is specified"),
        new("RemSound.App/KeyClickService.cs",
            "the key-click output stopping is stood in for by setting what its handler sets, because a test cannot take a "
            + "sound device away; the rebuild that follows is driven through the key-press routine",
            "KEY CLICKS"),
        new("RemSound.App/RelayConnectDialog.cs", "the dialog suite, and the relay connect step",
            "Dialog control suite", "Connecting to a relay, remembering it and ticking who is on it"),
        new("RemSound.App/ServiceSendHost.cs",
            "Windows saying the network changed, through the routine its handlers call, with the real service loop running; "
            + "the subscription itself is read from source, because a test cannot make Windows' network change",
            "The service looks a name up again until it answers"),
        new("RemSound.App/ServiceNetworkPresence.cs",
            "the relay member list changing, through the routine its handler calls; the subscription itself is read from "
            + "source, because the service cannot be started here",
            "The send-only service, and somebody ticking it on a relay", "The app and the send-only service both take part in relay groups"),
        new("RemSound.App/MainForm.Relay.cs",
            "the control suite drives the tab's controls; the relay step goes in after the address lookup, through the routine "
            + "the Connect button's handler finishes in, and drives the remembered list and the tick list through their handlers",
            "Control suite - ", "Connecting to a relay, remembering it and ticking who is on it"),
        new("RemSound.App/MainFormHotkeyController.cs", "every Alt key, a hotkey change, and the Keyboard shortcuts window in the dialog suite",
            "Keyboard: every Alt key goes where it says", "AUDIT: changing a hotkey does not claim", "Dialog control suite"),
        new("RemSound.App/MainFormTrayController.cs",
            "the tray icon's right press and the menu closing are driven; the menu items call the same toggles, restore and exit "
            + "the control suite drives",
            "HEADLESS: the tray menu still opens"),

        // ---- Dialogs. The dialog suite enumerates every window in the app and audits every
        // interactive control in each, so these are covered by construction rather than by listing. --
        new("RemSound.App/AboutDialog.cs", "the dialog suite", "Dialog control suite"),
        new("RemSound.App/AddBandDialog.cs", "the dialog suite", "Dialog control suite"),
        new("RemSound.App/ManualPeerPrompt.cs", "the dialog suite", "Dialog control suite"),
        new("RemSound.App/PreferencesDialog.cs", "the dialog suite, and the sounds", "Dialog control suite", "Every sound is pinned"),
        new("RemSound.App/ProfilePasswordDialog.cs", "the dialog suite", "Dialog control suite"),
        new("RemSound.App/ProfileSaveAsPrompt.cs", "the dialog suite", "Dialog control suite"),
        new("RemSound.App/ProfileSelectionDialog.cs", "the dialog suite", "Dialog control suite"),
        new("RemSound.App/QuickProfileSwitchDialog.cs", "the dialog suite", "Dialog control suite"),
        new("RemSound.App/RecordingSettingsDialog.cs", "the dialog suite", "Dialog control suite"),
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
            "an output lost under the playout (the handler that notices it) re-opens, and the churn starts and stops outputs",
            "AUDIT: an output Windows invalidated re-opens itself", "Lifecycle churn"),
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
