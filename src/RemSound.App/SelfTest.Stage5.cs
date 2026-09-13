using System.Text.RegularExpressions;
using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// The 2026-09-13 review, stage 5: words on screen and in messages that had fallen behind the app.
/// </summary>
internal static partial class SelfTest
{
    /// <summary>
    /// THE NEWEST ABOUT NOTES COVER THE WHOLE RELEASE.
    ///
    /// <para>2026-09-13 review (Dialogs #5). The v6.0 block described the plugin and nothing else. The release notes
    /// also cover the switch that stops plugins connecting, a peer's pan and EQ reaching the track, the network-delay
    /// limit, and a long list of fixes. The notes name two menu items, so those must still be called that.</para>
    /// </summary>
    private static string? AuditAboutNewestNotesCoverTheRelease()
    {
        var lines = AboutDialog.ReleaseNotesForTest.Replace("\r\n", "\n").Split('\n');
        Check(lines.Length > 0 && lines[0].StartsWith("RemSound v", StringComparison.Ordinal), "the About notes must start with a release heading");
        var next = Array.FindIndex(lines, 1, l => l.StartsWith("RemSound v", StringComparison.Ordinal));
        var newest = string.Join("\n", next < 0 ? lines : lines[..next]);
        foreach (var phrase in new[] { "Apply pan and EQ to plugin audio", "Let plugins connect to RemSound", "network delay", "The rest of this release" })
            Check(newest.Contains(phrase, StringComparison.Ordinal),
                $"the newest About notes must say what the release notes say — \"{phrase}\" is missing");

        var root = FindSourceRoot();
        if (root is null) return Skip("the notes cover the release, but the source tree is not reachable to check the menu names (set REMSOUND_SOURCE_ROOT, as run-tests.ps1 does)");
        var main = File.ReadAllText(Path.Combine(root, "src", "RemSound.App", "MainForm.cs"));
        foreach (var name in new[] { "Apply pan and EQ to plugin audio", "Let plugins connect to RemSound" })
            Check(main.Contains($"AccessibleName = \"{name}\"", StringComparison.Ordinal),
                $"the About notes name \"{name}\", so the DAW plugin menu item must still be called that");
        return $"{lines[0]} covers the plugin, its switch, pan and EQ, the delay limit and the other fixes; both menu names it quotes exist";
    }

    /// <summary>
    /// THE ABOUT NOTES CALL THE JITTER BUFFER BY ITS NAME.
    ///
    /// <para>2026-09-13 review (Dialogs #5). The control was renamed from latency to jitter buffer on 2026-08-23, because
    /// Total latency is a different number, but the v5.9 notes still called it the audio latency control.</para>
    /// </summary>
    private static string? AuditAboutNotesCallTheJitterBufferByItsName()
    {
        var shown = AboutDialog.TrimToLastVersions(AboutDialog.ReleaseNotesForTest, 5);
        Check(!shown.Contains("latency control", StringComparison.OrdinalIgnoreCase),
            "the About box must not call the jitter buffer the \"latency control\" — Total latency is a different number");
        return "no shown release calls the jitter buffer the latency control";
    }

    /// <summary>
    /// THE COLOUR THEME'S NEXT-LAUNCH HINT REACHES A SCREEN READER.
    ///
    /// <para>2026-09-13 review (Dialogs #13). "Takes effect next launch" was a grey label beside the box, which a screen
    /// reader never reaches, so choosing a theme and hearing nothing change looked broken.</para>
    /// </summary>
    private static string? AuditThemeHintReachesAScreenReader()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "remsound-themehint-" + Guid.NewGuid().ToString("N"));
        using var userData = AppConfig.UseThrowawayUserDataDirectory(scratch);
        var restoreMuted = CuePlayer.GloballyMuted;
        var restoreChecks = CheckSoundService.Suppressed;
        var restoreSink = UiChangeLog.Sink;
        CuePlayer.GloballyMuted = true;
        CheckSoundService.Suppressed = true;
        UiChangeLog.Sink = (_, _) => { };
        try
        {
            var factory = DialogFactories().First(f => f.Name == "Preferences");
            using var prefs = Require(factory.Make() as PreferencesDialog, "the Preferences factory must build a PreferencesDialog");
            var theme = Require(Walk(prefs).OfType<ComboBox>().FirstOrDefault(c => (c.AccessibleName ?? "").StartsWith("Colour theme", StringComparison.Ordinal)),
                "the Preferences window must have a colour theme box — this check would prove nothing");
            Check(theme.AccessibleName!.Contains("next launch", StringComparison.Ordinal),
                $"the colour theme box must tell a screen reader it takes effect next launch (it reads \"{theme.AccessibleName}\")");
            return $"the colour theme box reads \"{theme.AccessibleName}\"";
        }
        finally
        {
            CuePlayer.GloballyMuted = restoreMuted;
            CheckSoundService.Suppressed = restoreChecks;
            UiChangeLog.Sink = restoreSink;
        }

        static IEnumerable<Control> Walk(Control root)
        {
            foreach (Control c in root.Controls)
            {
                yield return c;
                foreach (var d in Walk(c)) yield return d;
            }
        }
    }

    /// <summary>
    /// THE MISSING-MANUAL MESSAGE SPEAKS TO USERS, NOT BUILDERS.
    ///
    /// <para>2026-09-13 review (App #21). Pressing F1 with the manual missing told the user to re-publish the build.</para>
    /// </summary>
    private static string? AuditHelpMessageSpeaksToUsers()
    {
        var root = FindSourceRoot();
        if (root is null) return Skip("the source tree is not reachable (set REMSOUND_SOURCE_ROOT, as run-tests.ps1 does)");
        var help = File.ReadAllText(Path.Combine(root, "src", "RemSound.App", "HelpLauncher.cs"));
        Check(help.Contains("Manual not found at:", StringComparison.Ordinal), "the missing-manual message is gone — this check is watching nothing");
        Check(!help.Contains("Re-publish", StringComparison.OrdinalIgnoreCase),
            "the missing-manual message must not tell a user to Re-publish the build — say how a user gets the file back");
        return "the missing-manual message tells a user to unzip or reinstall";
    }

    /// <summary>
    /// PREFERENCES MESSAGES NAME CONTROLS THAT EXIST.
    ///
    /// <para>2026-09-13 review (Dialogs #13). With no sound set, Play said to use "the Browse button on this row", but the
    /// cue section has one Browse button and no rows of buttons. The start-with-a-profile message pointed at "File menu
    /// -> Save profile as", and the menu item says Save as.</para>
    /// </summary>
    private static string? AuditPreferencesMessagesNameRealControls()
    {
        var root = FindSourceRoot();
        if (root is null) return Skip("the source tree is not reachable (set REMSOUND_SOURCE_ROOT, as run-tests.ps1 does)");
        var prefs = File.ReadAllText(Path.Combine(root, "src", "RemSound.App", "PreferencesDialog.cs"));
        var main = File.ReadAllText(Path.Combine(root, "src", "RemSound.App", "MainForm.cs"));
        Check(!prefs.Contains("Browse button on this row", StringComparison.Ordinal),
            "the no-sound message must not point at \"the Browse button on this row\" — there is one Browse button, below the list");
        Check(prefs.Contains("AccessibleName = \"Choose sound\"", StringComparison.Ordinal),
            "the no-sound message names the Choose sound list, so the list must still be called that");
        Check(prefs.Contains("(File menu, Save as)", StringComparison.Ordinal) && !prefs.Contains("Save profile as)", StringComparison.Ordinal),
            "the no-profiles message must name the menu item as it reads: File menu, Save as");
        Check(main.Contains("new ToolStripMenuItem(\"Save &as...\")", StringComparison.Ordinal),
            "the no-profiles message names Save as, so the File menu item must still say that");
        return "the no-sound and no-profiles messages name the Choose sound list, the Browse button and File, Save as";
    }

    /// <summary>
    /// THE JITTER BUFFER BOX HAS ONE NAME, WHATEVER IT STARTS AS.
    ///
    /// <para>2026-09-13 review (docs pass). The box was created as "Jitter buffer in milliseconds" and set again to that
    /// in the constructor, while its label and every later rename say "Audio jitter buffer" or name the lane. A
    /// screen reader could hear a name the screen never shows.</para>
    /// </summary>
    private static string? AuditJitterBufferHasOneName()
    {
        var root = FindSourceRoot();
        if (root is null) return Skip("the source tree is not reachable (set REMSOUND_SOURCE_ROOT, as run-tests.ps1 does)");
        var main = File.ReadAllText(Path.Combine(root, "src", "RemSound.App", "MainForm.cs"));
        Check(main.Contains("wasapiLatencyLabel.Text = \"Audio jitter buffer in milliseconds (Alt+&L)\"", StringComparison.Ordinal),
            "the jitter buffer label's wording has moved — this check is watching nothing");
        var stale = Regex.Matches(main, "\"Jitter buffer in milliseconds").Count;
        Check(stale == 0,
            $"the jitter buffer box must never be named \"Jitter buffer in milliseconds\" — its label says Audio jitter buffer ({stale} left)");
        return "every name the jitter buffer box is given matches a label on screen";
    }
}
