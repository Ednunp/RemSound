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
    /// <summary>How a file's wired handlers are covered. <paramref name="CoveredBy"/> names the suite
    /// that drives them, or begins "no test:" with the reason none is needed.</summary>
    private sealed record EventClaim(string File, string CoveredBy);

    private static readonly EventClaim[] EventClaims =
    [
        // ---- The main window and its controls ----------------------------------------------------
        // Driven for real by the control suite, in all three audio configurations: every control on
        // the window is enumerated from the live form, so a handler on a new control cannot hide.
        new("RemSound.App/MainForm.cs", "Control suite (3 configurations) + Every control is specified"),
        new("RemSound.App/RelayConnectDialog.cs", "Dialog control suite"),
        new("RemSound.App/ServiceNetworkPresence.cs",
            "The send-only service, and somebody ticking it on a relay (drives the one thing it subscribes to - the relay's " +
            "member list changing - through the routine that handler calls) + The app and the send-only service both take part " +
            "in relay groups (holds the subscription itself, read from source, because the service cannot be started here)"),
        new("RemSound.App/MainForm.Relay.cs",
            "Control suite (3 configurations) + Every control is specified + 'Connecting to a relay, remembering it and ticking who is on it', which drives the address box, the Connect button, the remembered list and the tick list through the real handlers"),
        new("RemSound.App/MainFormHotkeyController.cs", "Keyboard shortcut steps (keyboard shortcuts go where they say; a hotkey change leaves the profile clean; the Keyboard shortcuts window in the dialog suite)"),
        new("RemSound.App/MainFormTrayController.cs",
            "no test: its menu items call the same send and receive toggles, window restore and exit the control suite drives; " +
            "the tray icon itself needs a notification area, which a gate run must not create"),

        // ---- Dialogs. The dialog suite enumerates every window type in the app and audits every
        // interactive control in each, so these are covered by construction rather than by listing. --
        new("RemSound.App/AboutDialog.cs", "Dialog control suite"),
        new("RemSound.App/AddBandDialog.cs", "Dialog control suite"),
        new("RemSound.App/ManualPeerPrompt.cs", "Dialog control suite"),
        new("RemSound.App/PreferencesDialog.cs", "Dialog control suite + cue coverage"),
        new("RemSound.App/ProfilePasswordDialog.cs", "Dialog control suite"),
        new("RemSound.App/ProfileSaveAsPrompt.cs", "Dialog control suite"),
        new("RemSound.App/ProfileSelectionDialog.cs", "Dialog control suite"),
        new("RemSound.App/QuickProfileSwitchDialog.cs", "Dialog control suite"),
        new("RemSound.App/RecordingSettingsDialog.cs", "Dialog control suite + recording tests"),
        new("RemSound.App/RenamePeerDialog.cs", "Dialog control suite"),
        new("RemSound.App/ServiceProfileDialog.cs", "Dialog control suite + service tests"),
        new("RemSound.App/UpdateInstallNoticeDialog.cs", "Dialog control suite + update window tests"),
        new("RemSound.App/AsioLoadingSplash.cs", "Dialog control suite"),

        // ---- Shared accessible controls ------------------------------------------------------------
        new("RemSound.App/CheckedListAccessibility.cs", "Control suite (checked lists announce on every arrow press)"),
        new("RemSound.Ui/FormLayoutRows.cs", "Control suite (label-to-control association and click-to-focus)"),
        new("RemSound.Ui/PluginEditorPanel.cs", "Plugin window test (peer list, job, bypass)"),
        new("RemSound.Core/HotkeyCaptureForm.cs", "Keyboard shortcut suite"),

        // ---- The plugin -----------------------------------------------------------------------------
        new("RemSound.Plugin/RemSoundPlugin.cs", "Plugin parameters + end-to-end bridge tests"),
        new("RemSound.Core/PluginBridgeClient.cs", "App-plugin link + end-to-end bridge tests"),
        new("RemSound.Core/PluginBridgeHost.cs", "App-plugin link + end-to-end bridge tests"),

        // ---- Engine and services ---------------------------------------------------------------------
        new("RemSound.Core/PeerDiscoveryService.cs", "Discovery steps (a malformed broadcast is dropped; a remembered peer follows discovery)"),
        new("RemSound.Receiver/MultiOutputPlayout.cs", "Playout and lifecycle-churn tests"),
        new("RemSound.Core/SharedAsioDevice.cs", "Lifecycle churn (ASIO leg skipped without REMSOUND_TEST_ASIO), and the real-driver duplex step when REMSOUND_ASIO_TEST_DRIVER names one"),
        new("RemSound.Sender/AudioSessionStartWatcher.cs", "Session-start watcher lifecycle, send-app capture change detection, and the service waiting for a chosen application"),
        new("RemSound.Sender/CaptureSource.cs", "Capture steps (two sources stay aligned; the mixer never waits on its lock; drift counted in the mixer's units; capture survives a dead device event)"),
        new("RemSound.Sender/PushModeWasapiBackend.cs", "Push-mode eligibility, integer capture, and lifecycle churn"),
        new("RemSound.App/RecordingController.cs", "Recording suites - multi-peer split, modes by content, and the dying-writer report"),
        new("RemSound.App/CuePlayer.cs", "Every sound is pinned"),
        new("RemSound.App/PowerResumeHandler.cs",
            "no test: it only forwards Windows' resume notification. The wake steps call the path it forwards to directly, " +
            "because a gate run cannot put the machine to sleep"),

        // ---- Deliberately untested, with the reason ---------------------------------------------------
        new("RemSound.App/Program.cs",
            "no test: process-wide last-resort handlers (unhandled exception, unobserved task, application exit). " +
            "Driving them means crashing the test process, and what they do — write a crash file and get out — is " +
            "checked by the crash-report cap test instead"),
        new("RemSound.App/AppInstaller.cs",
            "no test: fires while an elevated installer copies files over the running app. Exercising it for real " +
            "would install RemSound on the machine running the gate"),
        new("RemSound.App/CommandLine.cs",
            "no test: the standalone plugin window's job callback, which does nothing by design — that window " +
            "carries no audio, and the status text says so"),
        new("RemSound.App/RouterPortMapper.cs",
            "no test: UPnP negotiation with whatever router is on the network. A test would either need a real " +
            "router or a fake one proving nothing about real ones"),
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
