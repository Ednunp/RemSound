using System.Windows.Forms;
using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// DIALOGS — the same standard as the main window, applied to every dialog the app can show.
///
/// <para>Ed, 2026-08-15: "go through every single line of code... pin a test to everything associated
/// to every control, every dialogue, every sound, every single bloody event."</para>
///
/// <para>The old dialog audit proved controls EXISTED — accessible name, mnemonic, tab order. That is
/// the same weak standard that let a completely dead latency slider pass for months. This adds the two
/// dimensions it never had: THEME (no hardcoded colour that breaks in light or dark) and EFFECT (drive
/// the control, prove something actually changed), and it counts every interactive control so none can
/// hide.</para>
///
/// <para>Dialog controls are built inline as locals rather than named fields, so they are identified by
/// what the USER sees — accessible name or caption — which is the right identity for this anyway.</para>
///
/// <para>Everything runs inside a THROWAWAY user-data folder. The Preferences dialog persists on
/// change, and a gate run must never edit the real configuration as a side effect of proving that a
/// checkbox works.</para>
/// </summary>
internal static partial class SelfTest
{
    /// <summary>Captions that are dialog plumbing rather than settings — controls that act rather than
    /// store. Declared here so the exemption is reviewable instead of silent.</summary>
    private static readonly string[] InertDialogControlNames =
    [
        "cancel", "close", "ok", "browse", "help", "test", "preview", "play", "reset",
        "clear", "remove", "add", "delete", "rename", "save", "apply", "install", "later", "yes", "no",
    ];

    private static string DialogControlName(Control c) =>
        (!string.IsNullOrWhiteSpace(c.AccessibleName) ? c.AccessibleName : c.Text ?? "").Trim();

    private static bool IsDeclaredInert(Control c)
    {
        var name = DialogControlName(c).ToLowerInvariant().Replace("&", "");
        if (name.Length == 0) return true;
        return Array.Exists(InertDialogControlNames, n => name == n || name.StartsWith(n + " ", StringComparison.Ordinal));
    }

    /// <summary>The whole-dialog sweep: every dialog, every control, four dimensions.</summary>
    private static string? DialogControlSuite()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "remsound-dialog-suite-" + Guid.NewGuid().ToString("N"));
        using var scope = AppConfig.UseThrowawayUserDataDirectory(scratch);

        // The suite's own profile settings and a stand-in for the Windows start-up entry, so everything a control can change
        // is visible to the snapshot below — and this machine's REAL start-up entry is never touched. 2026-09-13 review.
        var auditSettings = new RemSoundSettingsStore("RemSound");
        var realRunValueBefore = StartupAutoStart.RealRegistryValueForTest();
        var restoreAutoStartMemory = StartupAutoStart.UseMemoryForTest;
        StartupAutoStart.UseMemoryForTest = true;
        StartupAutoStart.MemoryValueForTest = null;

        var restoreMuted = CuePlayer.GloballyMuted;
        var restoreChecks = CheckSoundService.Suppressed;
        var restoreSink = UiChangeLog.Sink;
        var restoreKeyClicks = KeyClickService.Enabled;
        CuePlayer.GloballyMuted = true;
        CheckSoundService.Suppressed = true;
        try
        {
            var problems = new List<string>();
            var dialogs = 0;
            var controlsAudited = 0;
            var effectsProven = 0;
            // Which dialog controls actually change stored configuration, and which of those SAY so.
            // Ed, 2026-08-24: "let's do the dialogues next let's complete it." The main window's rule
            // is per-control because its controls are enumerated in a spec table; a dialog sweep has
            // no such table, so the rule here is derived from behaviour instead — if driving it moved
            // stored configuration, it changed something a user did on purpose, and that must leave a
            // trace. New dialog controls are covered the moment they are added, with nothing to keep
            // up to date.
            var changedConfig = new List<string>();
            var loggedIt = new List<string>();
            var dialogLogCapture = new List<string>();

            UiChangeLog.Sink = (what, value) => { lock (dialogLogCapture) dialogLogCapture.Add($"{what} → {value}"); };

            foreach (var (name, make) in DialogFactories(auditSettings))
            {
                // Name it BEFORE touching it: if a dialog blocks (a modal opened by a control we
                // drove, a driver probe), the last line of output names the culprit instead of
                // leaving a silent hang. The suite's first run hung here with no clue which one.
                Form? dlg = null;
                try { dlg = make(); }
                catch (Exception ex) { problems.Add($"{name}: could not be constructed - {ex.GetType().Name}: {ex.Message}"); continue; }
                dialogs++;
                try
                {
                    // Force the window handle. A form that was never shown has none, and any handler
                    // that marshals to the UI thread (BeginInvoke) throws instead of running — which
                    // would report a perfectly good control as broken.
                    try { _ = dlg.Handle; } catch { }
                    var all = new List<Control>();
                    void Walk(Control p) { foreach (Control c in p.Controls) { all.Add(c); Walk(c); } }
                    Walk(dlg);

                    foreach (var c in all)
                    {
                        // (3) THEME — the dimension the old audit never had. A hardcoded colour is
                        // unreadable in one of the two modes, and nobody can see that from a log.
                        if (!IsThemeApprovedColour(c.ForeColor))
                            problems.Add($"{name} / '{DialogControlName(c)}': hardcoded ForeColor {c.ForeColor} - fights light/dark");
                        if (!IsThemeApprovedColour(c.BackColor))
                            problems.Add($"{name} / '{DialogControlName(c)}': hardcoded BackColor {c.BackColor} - fights light/dark");

                        var interactive = c is CheckBox or ComboBox or NumericUpDown or TrackBar or ListBox or CheckedListBox;
                        if (!interactive) continue;
                        controlsAudited++;

                        // (2) ACCESSIBILITY — it must announce something.
                        if (string.IsNullOrWhiteSpace(DialogControlName(c)))
                            problems.Add($"{name}: a {c.GetType().Name} has nothing for a screen reader to announce");

                        // (4) EFFECT — drive it and see whether any persisted state moved. Dialogs that
                        // persist on change (Preferences, the big one) prove themselves right here; the
                        // rest persist on OK and are counted separately rather than silently skipped.
                        if (!c.Enabled) continue;
                        var before = SnapshotConfig(auditSettings);
                        // (5) THE LOG. Captured around the drive, so a control that changes stored
                        // configuration can be required to SAY so — see below.
                        dialogLogCapture.Clear();
                        try { DriveDialogControl(c); }
                        catch (Exception ex) { problems.Add($"{name} / '{DialogControlName(c)}': driving it threw {ex.GetType().Name}: {ex.Message}"); continue; }
                        if (SnapshotConfig(auditSettings) == before) continue;
                        effectsProven++;
                        changedConfig.Add($"{name} / '{DialogControlName(c)}'");
                        if (dialogLogCapture.Count > 0) loggedIt.Add($"{name} / '{DialogControlName(c)}'");
                    }
                }
                finally { try { dlg.Dispose(); } catch { } }
            }

            // Assert the POSITIVE facts, not only the absence of problems. Reporting "no problems"
            // after auditing nothing is the exact failure this suite exists to catch, and until
            // 2026-08-24 its passing path executed no assertion at all: had DialogFactories() come
            // back empty, or every dialog stopped exposing controls, it would still have returned a
            // confident summary.
            var declared = DialogFactories().Count();
            Check(dialogs == declared,
                $"every declared dialog must have been built and audited ({dialogs} of {declared}) — a dialog that "
                + "quietly drops out of this sweep is a dialog nothing checks");
            Check(controlsAudited > 0,
                "the sweep found no interactive control in any dialog — it has stopped walking them, and 'no problems' "
                + "over nothing is not a pass");
            Check(effectsProven > 0,
                $"not one of the {controlsAudited} controls changed any persisted state when driven — EFFECT is the whole "
                + "point of this suite over the old existence-only audit, and it proved nothing this run");
            // (5) AND THE LOG. Any dialog control that CHANGED stored configuration changed something
            // the user did on purpose, and that must leave a trace. Derived from behaviour rather
            // than from a spec table, so a dialog control added tomorrow is covered the moment it
            // persists anything — there is nothing to keep up to date and nothing to forget.
            var silent = changedConfig.Except(loggedIt).ToList();
            Check(silent.Count == 0,
                $"{silent.Count} dialog control(s) changed stored configuration and recorded NOTHING: "
                + $"{string.Join(", ", silent)}. A setting a user changed on purpose, with no trace, is a setting "
                + "nobody can account for afterwards — and the night a log matters is never the night there is time "
                + "to add one");
            Check(loggedIt.Count > 0,
                "not one dialog control was proven to log what it changed — the log half of this suite proved nothing");

            Check(StartupAutoStart.RealRegistryValueForTest() == realRunValueBefore,
                "driving the dialogs must leave this machine's real 'start RemSound when you sign in' entry exactly as it was — "
                + "the suite ticks that box like any other, and until 2026-09-13 it rewrote the real entry on every run");

            Check(problems.Count == 0, string.Join("; ", problems));

            return $"{dialogs} dialogs, {controlsAudited} interactive controls audited (accessibility + theme + driven); "
                 + $"{effectsProven} persisted a change on the spot, {controlsAudited - effectsProven} persist on OK or are plumbing; "
                 + $"all {loggedIt.Count} that persisted a change also LOGGED it";
        }
        finally
        {
            CuePlayer.GloballyMuted = restoreMuted;
            CheckSoundService.Suppressed = restoreChecks;
            UiChangeLog.Sink = restoreSink;
            KeyClickService.Enabled = restoreKeyClicks;
            StartupAutoStart.UseMemoryForTest = restoreAutoStartMemory;
            StartupAutoStart.MemoryValueForTest = null;
        }
    }

    /// <summary>Everything a dialog control can change, as text, so "did driving this control change anything" is
    /// answerable without knowing which setting each control maps to: the machine-wide configuration, the profile settings
    /// the dialogs were given, and the start-up entry. Until 2026-09-13 only the first — so a profile setting (accept remote
    /// volume, a cue turned off) or the start-up entry could change with nothing logged and the suite stayed green.</summary>
    private static string SnapshotConfig(RemSoundSettingsStore settings)
    {
        try
        {
            return System.Text.Json.JsonSerializer.Serialize(AppConfig.Load())
                + "|profile:" + settings.SnapshotForTest()
                + "|start-up entry:" + (StartupAutoStart.MemoryValueForTest ?? "");
        }
        catch { return ""; }
    }

    private static void DriveDialogControl(Control c)
    {
        switch (c)
        {
            case CheckBox cb: cb.Checked = !cb.Checked; break;
            case NumericUpDown n: n.Value = n.Value < n.Maximum ? n.Value + 1 : n.Minimum; break;
            case TrackBar t: DragSlider(t, t.Value < t.Maximum ? 1 : -1); break;
            case CheckedListBox clb when clb.Items.Count > 0: clb.SetItemChecked(0, !clb.GetItemChecked(0)); break;
            case ComboBox or ListBox: SelectNext(c); break;
        }
    }

    /// <summary>Every window the app can show must be reachable by the audits. A dialog with no
    /// headless construction path is invisible to every test — which is exactly how the
    /// single-instance prompt went unaudited. Reflect over the assembly and require each Form type
    /// to be covered by the shared factory list.</summary>
    private static string? EveryDialogIsAudited()
    {
        var formTypes = typeof(MainForm).Assembly.GetTypes()
            .Where(t => typeof(Form).IsAssignableFrom(t) && !t.IsAbstract && t != typeof(MainForm))
            .Select(t => t.Name)
            .ToList();

        var covered = new HashSet<string>(StringComparer.Ordinal);
        var brokenFactories = new List<string>();
        foreach (var (factoryName, make) in DialogFactories())
        {
            // Do NOT swallow: a factory that throws leaves its dialog untested while this guard
            // reports everything fine — the exact "passes while proving nothing" failure.
            try { var f = make(); covered.Add(f.GetType().Name); f.Dispose(); }
            catch (Exception ex) { brokenFactories.Add($"{factoryName} ({ex.GetType().Name})"); }
        }
        Check(brokenFactories.Count == 0,
            $"these dialog factories throw, so their dialogs are audited by nothing: {string.Join(", ", brokenFactories)}");

        // A window type is covered when some factory produces it — CmdKeyForm is the shared host for
        // every closure-built dialog, so it is reached through those, not by a factory of its own.
        var missing = formTypes.Where(t => !covered.Contains(t)).ToList();
        Check(missing.Count == 0,
            $"these windows are shown to users but no audit can reach them - give each a headless Build seam: {string.Join(", ", missing)}");
        return $"{formTypes.Count} window types in the app, all reachable by the audits";
    }
}
