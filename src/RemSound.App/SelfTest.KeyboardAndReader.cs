using System.Reflection;
using System.Text.RegularExpressions;
using NAudio.Wave;
using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// KEYBOARD AND SCREEN READER — the fixes from the 2026-09-13 review, each pinned by what someone using the
/// keyboard or NVDA actually meets, rather than by the code that happens to implement it today.
/// </summary>
internal static partial class SelfTest
{
    /// <summary>
    /// EVERY ALT KEY GOES WHERE IT SAYS — IN EVERY TAB, EVERY DIALOG, AND ALL THREE AUDIO CONFIGURATIONS.
    ///
    /// <para>The 2026-09-13 review found seven Alt keys that did not do what the screen said, with the gate green
    /// through all of them. Its mnemonic check compared letters only among controls sharing one parent, and never
    /// asked whether a promise like "(Alt+M)" was kept. Total latency said Alt+M while its marked letter was T;
    /// Alt+I had two owners on one tab; Alt+P went to the codec box; each label in Add EQ band sent its key to the
    /// box of the row below; Alt+N in Rename peer landed on "Clear custom name"; and Alt+A in Preferences switched
    /// remote volume commands on or off from the auto-save list.</para>
    ///
    /// <para><b>What an Alt key can reach.</b> Only controls that are showing: the selected tab, and whatever sits
    /// outside the tabs. So every tab page is a scope of its own together with everything outside the tab control,
    /// and a control hidden in the current configuration is left out. That is why the main window is walked in all
    /// three configurations — the jitter-buffer rows change their letters, and whether they show, with it.</para>
    ///
    /// <para><b>Within a scope:</b> a letter belongs to one control; a caption that says "(Alt+X)" has X marked; and a
    /// control that cannot carry a letter of its own — a list, a box, a readout — and promises "(Alt+X)" in the name
    /// NVDA reads must be where X actually LANDS. A plain label sends its key to the next control in tab order that
    /// can take focus; a MnemonicLabel sends it to its target. Those are the two rules WinForms follows.</para>
    /// </summary>
    private static string? KeyboardShortcutsGoWhereTheySay()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "remsound-altkeys-" + Guid.NewGuid().ToString("N"));
        using var userData = AppConfig.UseThrowawayUserDataDirectory(scratch);
        var restoreMuted = CuePlayer.GloballyMuted;
        var restoreChecks = CheckSoundService.Suppressed;
        CuePlayer.GloballyMuted = true;
        CheckSoundService.Suppressed = true;
        try
        {
            Require(ControlGetStateMethod,
                "Control.GetState was not found, so a hidden control cannot be told from a showing one and every scope would be wrong");
            Require(ControlGetStyleMethod, "Control.GetStyle was not found, so where a plain label's key lands cannot be followed");

            var problems = new List<string>();
            var scopes = 0;
            var promises = 0;

            MainForm form;
            try { form = new MainForm(null, Profile.NewBlank(), null, null, headless: true); }
            catch (Exception ex) { return Skip($"headless MainForm could not be constructed: {ex.GetType().Name}: {ex.Message}"); }
            using (form)
            {
                var asioRowField = Require(typeof(MainForm).GetField("asioLatencyLabel", BindingFlags.Instance | BindingFlags.NonPublic),
                    "MainForm.asioLatencyLabel not found — the premise that hidden controls are recognised cannot be checked");
                var asioRowLabel = Require(asioRowField.GetValue(form) as Control, "MainForm.asioLatencyLabel is not a control");
                foreach (var configuration in AudioConfigurations.All)
                {
                    SetAudioConfigurationForTest(form, configuration);
                    InvokeWindowMethodForAltAudit(form, "UpdateBothIndependentVisibility");
                    InvokeWindowMethodForAltAudit(form, "UpdateAutoTuneIntervalWording");
                    Check(IsSetVisibleForAltAudit(asioRowLabel) == configuration.UsesAsio(),
                        $"{configuration.Describe()}: premise — the ASIO jitter-buffer row must read as showing exactly when an ASIO driver "
                        + "is chosen, or this audit is looking at the wrong controls");
                    var (s, p) = AuditAltKeys($"Main window ({configuration.Describe()})", form, problems);
                    Check(s >= 3, $"{configuration.Describe()}: every main-window tab must be audited as a scope of its own (found {s})");
                    Check(p >= 10, $"{configuration.Describe()}: the main window's Alt promises must be found and followed (found {p})");
                    scopes += s;
                    promises += p;
                }
            }

            var dialogs = 0;
            foreach (var (name, make) in DialogFactories())
            {
                Form dialog;
                try { dialog = make(); }
                catch (Exception ex) { problems.Add($"{name}: could not be built ({ex.GetType().Name}: {ex.Message})"); continue; }
                using (dialog)
                {
                    var (s, p) = AuditAltKeys(name, dialog, problems);
                    scopes += s;
                    promises += p;
                    dialogs++;
                }
            }

            Check(dialogs == DialogFactories().Length, $"every dialog must be audited ({dialogs} of {DialogFactories().Length})");
            Check(problems.Count == 0, $"{problems.Count} Alt key problem(s): {string.Join("; ", problems)}");
            return $"{scopes} keyboard scopes — the main window in all three configurations and {dialogs} dialogs; {promises} written or "
                 + "spoken \"Alt+\" promises all lead where they say, and no letter has two owners";
        }
        finally
        {
            CuePlayer.GloballyMuted = restoreMuted;
            CheckSoundService.Suppressed = restoreChecks;
        }
    }

    private static (int Scopes, int Promises) AuditAltKeys(string formName, Form form, List<string> problems)
    {
        var outside = new List<Control>();
        var pages = new List<TabPage>();
        CollectShownForAltAudit(form, outside, pages);

        var scopes = new List<(string Name, List<Control> Controls)>();
        if (pages.Count == 0) scopes.Add((formName, outside));
        foreach (var page in pages)
        {
            var inScope = new List<Control>(outside);
            CollectShownForAltAudit(page, inScope, null);
            scopes.Add(($"{formName} / {page.Text.Replace("&", "")}", inScope));
        }

        var promises = 0;
        foreach (var (name, controls) in scopes) promises += AuditAltKeyScope(name, controls, problems);
        return (scopes.Count, promises);
    }

    /// <summary>Every showing control under <paramref name="parent"/>. A tab control's pages become scopes of their own
    /// when <paramref name="tabPages"/> is given; a tab control met inside a scope contributes the page it is showing.</summary>
    private static void CollectShownForAltAudit(Control parent, List<Control> into, List<TabPage>? tabPages)
    {
        foreach (Control c in parent.Controls)
        {
            if (!IsSetVisibleForAltAudit(c)) continue;
            if (c is TabControl tabs)
            {
                if (tabPages is not null)
                {
                    foreach (TabPage page in tabs.TabPages) tabPages.Add(page);
                }
                else if (tabs.SelectedTab is { } showing)
                {
                    CollectShownForAltAudit(showing, into, null);
                }
                continue;
            }
            into.Add(c);
            CollectShownForAltAudit(c, into, tabPages);
        }
    }

    private static int AuditAltKeyScope(string scope, List<Control> controls, List<string> problems)
    {
        var owners = new Dictionary<char, List<Control>>();
        var promises = 0;

        // Captions: labels, buttons, check boxes, group boxes. Their marked letter is their Alt key.
        foreach (var c in controls)
        {
            if (c is not (Label or ButtonBase or GroupBox)) continue;
            if (c is Label { UseMnemonic: false } or ButtonBase { UseMnemonic: false }) continue;
            var caption = c.Text ?? "";
            var letters = MnemonicLettersForAltAudit(caption);
            if (letters.Count > 1)
                problems.Add($"{scope} / \"{Plain(caption)}\": {letters.Count} letters are marked, and a caption can only have one working Alt key");
            // Every button has an Alt key — the About box has promised that since v5.5, and Close in Preferences, Cancel in
            // Rename peer and Cancel in the service profile had none.
            if (c is Button && letters.Count == 0 && !string.IsNullOrWhiteSpace(caption))
                problems.Add($"{scope} / \"{Plain(caption)}\": a button with no Alt key");
            // A field's label reads to NVDA as what it says on screen: "Choose sound" on screen was "Choose default sound"
            // to NVDA. Only labels that carry an Alt key are field labels; a readout label ("Status", "Connection
            // health") deliberately keeps a fixed name while its text changes.
            if (c is Label && letters.Count > 0 && !string.IsNullOrWhiteSpace(c.AccessibleName)
                && !Plain(caption).TrimStart().StartsWith(c.AccessibleName.Trim(), StringComparison.OrdinalIgnoreCase))
                problems.Add($"{scope} / \"{Plain(caption)}\": NVDA reads this label as \"{c.AccessibleName}\", which is not what the screen says");
            if (letters.Count > 0)
            {
                if (!owners.TryGetValue(letters[0], out var owned)) owners[letters[0]] = owned = [];
                owned.Add(c);
            }
            foreach (var promised in AltPromisesForAltAudit(caption))
            {
                promises++;
                if (letters.Count == 0)
                    problems.Add($"{scope} / \"{Plain(caption)}\": says Alt+{Upper(promised)} but no letter is marked, so the key does nothing");
                else if (letters[0] != promised)
                    problems.Add($"{scope} / \"{Plain(caption)}\": says Alt+{Upper(promised)} but its marked letter makes it Alt+{Upper(letters[0])}");
            }
        }

        foreach (var (letter, owned) in owners)
        {
            if (owned.Count < 2) continue;
            problems.Add($"{scope}: Alt+{Upper(letter)} belongs to {owned.Count} controls ({string.Join(", ", owned.Select(o => $"\"{Plain(o.Text)}\""))}) "
                + "— only one of them can ever get it");
        }

        // Lists, boxes and readouts cannot carry a letter, so they promise one in the name NVDA reads. That letter must
        // lead HERE, not merely exist somewhere on the tab.
        foreach (var c in controls)
        {
            if (c is Label or ButtonBase or GroupBox) continue;
            var name = c.AccessibleName ?? "";
            foreach (var promised in AltPromisesForAltAudit(name))
            {
                promises++;
                if (!owners.TryGetValue(promised, out var owned))
                {
                    problems.Add($"{scope} / \"{name}\": promises Alt+{Upper(promised)}, and nothing showing here answers that key");
                    continue;
                }
                var lands = WhereAltKeyLandsForAltAudit(owned[0]);
                var arrives = lands is not null && (ReferenceEquals(lands, c) || lands.Contains(c) || c.Contains(lands));
                if (!arrives)
                    problems.Add($"{scope} / \"{name}\": promises Alt+{Upper(promised)}, but that key lands on {DescribeForAltAudit(lands)}");
            }
        }
        return promises;

        static string Plain(string? text) => (text ?? "").Replace("&", "");
        static char Upper(char letter) => char.ToUpperInvariant(letter);
    }

    /// <summary>Where a control's Alt key actually puts focus, by the rules WinForms follows.</summary>
    private static Control? WhereAltKeyLandsForAltAudit(Control owner)
    {
        if (owner is MnemonicLabel mnemonic) return mnemonic.MnemonicTarget;
        if (owner is not (Label or GroupBox)) return owner;

        // A plain label: Label.ProcessMnemonic calls Parent.SelectNextControl(label, forward: true, tabStopOnly: false,
        // nested: true, wrap: false) — the next control in tab order, inside the label's parent, that can take focus.
        var parent = owner.Parent;
        if (parent is null) return null;
        for (var next = parent.GetNextControl(owner, true); next is not null; next = parent.GetNextControl(next, true))
        {
            if (IsFocusableForAltAudit(next, parent)) return next;
        }
        return null;
    }

    private static bool IsFocusableForAltAudit(Control c, Control within)
    {
        for (var x = c; x is not null && !ReferenceEquals(x, within); x = x.Parent)
        {
            if (!IsSetVisibleForAltAudit(x)) return false;
        }
        return (bool)ControlGetStyleMethod!.Invoke(c, [ControlStyles.Selectable])!;
    }

    private static string DescribeForAltAudit(Control? c) => c is null
        ? "nothing at all"
        : $"\"{(!string.IsNullOrWhiteSpace(c.AccessibleName) ? c.AccessibleName : c is Label or ButtonBase ? (c.Text ?? "").Replace("&", "") : "")}\" ({c.GetType().Name})";

    /// <summary>Every letter marked with an unescaped &amp;, in order. WinForms answers only the first.</summary>
    private static List<char> MnemonicLettersForAltAudit(string text)
    {
        var letters = new List<char>();
        for (var i = 0; i < text.Length - 1; i++)
        {
            if (text[i] != '&') continue;
            if (text[i + 1] == '&') { i++; continue; }
            if (char.IsLetterOrDigit(text[i + 1])) letters.Add(char.ToLowerInvariant(text[i + 1]));
        }
        return letters;
    }

    private static IEnumerable<char> AltPromisesForAltAudit(string text) =>
        AltPromisePattern().Matches(text).Select(m => char.ToLowerInvariant(m.Groups[1].Value[0]));

    [GeneratedRegex(@"\(Alt\+&?([A-Za-z0-9])\)")]
    private static partial Regex AltPromisePattern();

    private static readonly MethodInfo? ControlGetStateMethod = typeof(Control)
        .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
        .FirstOrDefault(m => m.Name == "GetState" && m.ReturnType == typeof(bool) && m.GetParameters().Length == 1);

    private static readonly MethodInfo? ControlGetStyleMethod = typeof(Control)
        .GetMethod("GetStyle", BindingFlags.Instance | BindingFlags.NonPublic, [typeof(ControlStyles)]);

    /// <summary>The control's OWN visible flag. <c>Control.Visible</c> is no use in a window that has never been shown:
    /// it reports false for everything.</summary>
    private static bool IsSetVisibleForAltAudit(Control c)
    {
        var method = ControlGetStateMethod ?? throw new InvalidOperationException("Control.GetState not found");
        var flagType = method.GetParameters()[0].ParameterType;
        object visibleFlag = flagType.IsEnum ? Enum.ToObject(flagType, 0x00000002) : 0x00000002;
        return (bool)method.Invoke(c, [visibleFlag])!;
    }

    private static void InvokeWindowMethodForAltAudit(object target, string methodName)
    {
        var method = Require(target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic),
            $"{target.GetType().Name}.{methodName} not found — the window cannot be put into the state this test means to audit");
        method.Invoke(target, null);
    }

    /// <summary>
    /// A RECORDING THAT CANNOT START MUST SAY SO IN FRONT, NOT BEHIND.
    ///
    /// <para>2026-09-13 review. A recording can be started from the global hotkey while RemSound is minimised. When it
    /// could not start, the message was an ownerless MessageBox, which opens behind whatever has focus — where NVDA
    /// never finds it, so the hotkey appeared to do nothing. Every such box in the recording controller goes through
    /// ForegroundDialog, the house rule for anything that can appear while RemSound is in the background.</para>
    ///
    /// <para>A source check, because the failure is where a window opens, and a gate run must not open one.</para>
    /// </summary>
    private static string? AuditRecordingStartFailureComesToTheFront()
    {
        var root = FindSourceRoot();
        if (root is null) return Skip("the source tree is not reachable from here (set REMSOUND_SOURCE_ROOT, as run-tests.ps1 does)");
        var lines = File.ReadAllLines(Path.Combine(root, "src", "RemSound.App", "RecordingController.cs"));
        var boxes = lines.Select((text, index) => (Text: text, Line: index + 1)).Where(l => l.Text.Contains("MessageBox.Show(")).ToList();
        Check(boxes.Count > 0,
            "the recording controller must still tell the user when a recording cannot start — no message box was found at all");
        // A box given the window it was opened from as its owner is already in front of that window. A box with NO
        // owner is the shape that opens behind everything, so it must come through ForegroundDialog.
        var behind = boxes.Where(l => !l.Text.Contains("ForegroundDialog.Show(owner => MessageBox.Show(")
                                      && !l.Text.Contains("MessageBox.Show(owner,")
                                      && !l.Text.Contains("MessageBox.Show(this,")).ToList();
        Check(behind.Count == 0,
            $"a message box in RecordingController.cs has no owner and does not go through ForegroundDialog (line "
            + $"{string.Join(", ", behind.Select(b => b.Line))}) — started from the hotkey while RemSound is minimised, it opens "
            + "behind other windows where a screen reader never finds it");
        return $"all {boxes.Count} of the recording controller's message boxes either come to the front or open over the window they came from";
    }

    /// <summary>
    /// ADDITIONAL SERVICE OPTIONS CAN BE CANCELLED, AND SAVES NOTHING UNTIL THE SERVICE DIALOG ITSELF IS ACCEPTED.
    ///
    /// <para>2026-09-13 review. The dialog had only an OK button, so Escape did nothing. And its OK wrote the startup
    /// volume to the machine-wide store at once — so cancelling the service dialog afterwards kept a change the user
    /// had just cancelled, although the dialog promises nothing is saved until its caller acts on OK.</para>
    ///
    /// <para>The store is only READ here. It is machine-wide and belongs to the real service, so nothing in the gate
    /// writes it.</para>
    /// </summary>
    private static string? AuditServiceAdditionalOptionsCanBeCancelled()
    {
        var (options, _, _) = ServiceProfileDialog.BuildAdditionalOptions(false);
        using (options)
        {
            Check(options.CancelButton is Button { DialogResult: DialogResult.Cancel } cancel && cancel.Parent is not null,
                "Additional service options must have a Cancel button that Escape presses — with only OK, Escape did nothing");
        }

        var stored = ServiceStore.LoadStartupVolume();
        using var service = new ServiceProfileDialog(Profile.NewBlank(), false);
        Check(service.PendingStartupVolume is null, "nothing may be pending before Additional options has been accepted");

        var wanted = (Enabled: !stored.Enabled, Percent: stored.Percent == 37 ? 38 : 37, BootOnly: !stored.BootOnly);
        service.AcceptAdditionalOptions(true, wanted.Enabled, wanted.Percent, wanted.BootOnly);

        var after = ServiceStore.LoadStartupVolume();
        Check(after == stored,
            $"accepting Additional options must NOT save the startup volume by itself (stored {stored}, now {after}) — it is saved "
            + "with the service profile, only when the service dialog is accepted as well");
        Check(service.PendingStartupVolume is { } pending
              && pending.Enabled == wanted.Enabled && pending.Percent == wanted.Percent && pending.BootOnly == wanted.BootOnly
              && service.ServiceLoggingEnabled,
            "the choices must be held for the service dialog's own OK, so its caller can save them");

        return "Additional service options has a Cancel that Escape presses, and its OK holds the startup volume for the service "
             + "dialog's OK instead of saving it";
    }

    /// <summary>
    /// CHOOSING A SOUND CHANGES WHAT YOU HEAR — AND YOUR OWN FILE CAN BE CHOSEN BACK.
    ///
    /// <para>2026-09-13 review. With your own file set for a cue, choosing a built-in sound in the Choose sound list
    /// was saved and previewed, and then your own file went on playing, because your own file always wins. Nothing
    /// said so. The list now shows your file as a row of its own, selected while it is what plays; choosing a built-in
    /// sound really switches; and your file's row stays in the list, so arrowing through the choices to hear them can
    /// never lose it.</para>
    /// </summary>
    private static string? AuditChoosingASoundIsWhatYouHear()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "remsound-choosesound-" + Guid.NewGuid().ToString("N"));
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
            var list = prefs.SoundListForTest;

            // A cue that has built-in sounds to choose from.
            var cue = -1;
            for (var i = 0; i < prefs.CueCountForTest && cue < 0; i++)
            {
                prefs.SelectCueForTest(i);
                if (list.Items.Count >= 2) cue = i;
            }
            Check(cue >= 0, "no cue has built-in sounds in the Choose sound list, so nothing below would prove anything");

            Directory.CreateDirectory(scratch);
            var ownFile = Path.Combine(scratch, "my own gate sound.wav");
            using (var writer = new WaveFileWriter(ownFile, new WaveFormat(48000, 16, 1))) writer.Write(new byte[960], 0, 960);

            prefs.UseOwnFileForTest(ownFile);
            Check(string.Equals(prefs.SoundThatWouldPlayForTest(), ownFile, StringComparison.OrdinalIgnoreCase),
                "premise: with your own file set for a cue, your own file is what plays");
            var ownRow = Enumerable.Range(0, list.Items.Count).FirstOrDefault(i => list.Items[i]?.ToString()?.Contains("my own gate sound") == true, -1);
            Check(ownRow > 0 && list.SelectedIndex == ownRow,
                $"the Choose sound list must show your own file as a row, selected while it is what plays (row {ownRow}, selected "
                + $"{list.SelectedIndex})");

            var builtIn = ownRow + 1;
            Check(builtIn < list.Items.Count, "there must be a built-in sound listed after your own file");
            list.SelectedIndex = builtIn;
            var nowPlays = prefs.SoundThatWouldPlayForTest();
            Check(nowPlays is not null && !string.Equals(nowPlays, ownFile, StringComparison.OrdinalIgnoreCase),
                $"choosing a built-in sound must change what plays — it is still your own file ({nowPlays ?? "nothing"}). That is the "
                + "bug: the choice was saved and previewed, and then never heard");
            Check(list.Items.Count > ownRow && list.Items[ownRow]?.ToString()?.Contains("my own gate sound") == true,
                "your own file's row must stay in the list after choosing a built-in sound, so arrowing back undoes it");

            list.SelectedIndex = ownRow;
            Check(string.Equals(prefs.SoundThatWouldPlayForTest(), ownFile, StringComparison.OrdinalIgnoreCase),
                "arrowing back onto your own file must bring it back — arrowing through the list to hear the choices must never lose it");

            return $"with your own file set, the list shows it as a selected row; choosing a built-in sound plays that sound "
                 + $"({Path.GetFileName(nowPlays)}); arrowing back brings your file back";
        }
        finally
        {
            CuePlayer.GloballyMuted = restoreMuted;
            CheckSoundService.Suppressed = restoreChecks;
            UiChangeLog.Sink = restoreSink;
        }
    }

    /// <summary>
    /// CHANGING A HOTKEY DOES NOT CLAIM THE PROFILE HAS UNSAVED CHANGES.
    ///
    /// <para>2026-09-13 review. Hotkeys have been saved on this computer, the moment they change, since v4.4. The window
    /// still marked the profile dirty on every hotkey change, so closing afterwards asked the user to save a profile that
    /// had nothing to save, and a warning told them the hotkey was "saved in your profile".</para>
    /// </summary>
    private static string? AuditHotkeyChangeLeavesTheProfileClean()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "remsound-hotkeystore-" + Guid.NewGuid().ToString("N"));
        using var userData = AppConfig.UseThrowawayUserDataDirectory(scratch);

        // Where a hotkey really lives: a second store, which shares no cache with the first, reads it back.
        var changed = new HotkeyInfo(Keys.F9, true, true, false);
        new RemSoundSettingsStore("RemSound").SaveTrayHotkey(changed);
        Check(new RemSoundSettingsStore("RemSound").LoadTrayHotkey().Equals(changed),
            "premise: a hotkey saved through one settings store must be read back by another — it is saved on this computer, not "
            + "held in a profile waiting for Save");

        MainForm form;
        try { form = new MainForm(null, Profile.NewBlank(), null, null, headless: true); }
        catch (Exception ex) { return Skip($"headless MainForm could not be constructed: {ex.GetType().Name}: {ex.Message}"); }
        using (form)
        {
            const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
            var controllerField = Require(typeof(MainForm).GetField("hotkeyController", Private), "MainForm.hotkeyController not found");
            var controller = Require(controllerField.GetValue(form) as MainFormHotkeyController,
                "MainForm.hotkeyController is not a hotkey controller");
            var unsaved = Require(typeof(MainForm).GetField("unsavedChanges", Private), "MainForm.unsavedChanges not found");
            Require(typeof(MainForm).GetField("applyingProfile", Private), "MainForm.applyingProfile not found").SetValue(form, false);

            unsaved.SetValue(form, false);
            var onChanged = Require(controller.OnHotkeyChanged,
                "the window must still listen for hotkey changes — it keeps the spoken \"press X anywhere\" hints in step");
            onChanged();
            Check(!(bool)unsaved.GetValue(form)!,
                "changing a hotkey must not mark the PROFILE as having unsaved changes — the hotkey is already saved on this computer, "
                + "so the prompt asks the user to save something that has nothing to save");

            Require(typeof(MainForm).GetMethod("MarkProfileDirty", Private), "MainForm.MarkProfileDirty not found").Invoke(form, null);
            Check((bool)unsaved.GetValue(form)!, "premise: the unsaved-changes flag must be settable, or the check above proves nothing");
        }
        return "a hotkey change leaves the profile clean, and the hotkey is read back from this computer's settings";
    }

    /// <summary>
    /// "CONNECTED" MEANS THE SAME IN THE CONNECT CUE, THE STATUS READOUT AND THE PEER LIST.
    ///
    /// <para>2026-09-13 review. The connect and disconnect cues counted a peer as connected while its audio was
    /// arriving OR its heartbeat was healthy, and held that through a heartbeat blip. The status readout and the
    /// connected-peers list asked the heartbeat alone. During a blip with audio still playing, the readout said "Not
    /// connected to any peer", the "connected for" time reset, and the list said the peer was not connected — while
    /// the cue, rightly, had not said disconnected.</para>
    /// </summary>
    private static string? AuditConnectedMeansTheSameEverywhere()
    {
        Check(MainForm.PeerConnectionRule(audioFlowing: true, PeerHealthState.Stale, wasConnected: false),
            "audio arriving means connected, whatever the heartbeat is doing");
        Check(MainForm.PeerConnectionRule(audioFlowing: false, PeerHealthState.Healthy, wasConnected: false),
            "a healthy heartbeat means connected, even in silence");
        Check(MainForm.PeerConnectionRule(audioFlowing: false, PeerHealthState.Stale, wasConnected: true),
            "a heartbeat blip with no audio holds a connected peer connected");
        Check(!MainForm.PeerConnectionRule(audioFlowing: false, PeerHealthState.Stale, wasConnected: false),
            "...and a peer that was not connected stays not connected");
        Check(!MainForm.PeerConnectionRule(audioFlowing: false, PeerHealthState.Unreachable, wasConnected: true),
            "a peer is lost only when its audio has stopped AND its heartbeat is unreachable");

        var root = FindSourceRoot();
        if (root is null) return Skip("the source tree is not reachable from here (set REMSOUND_SOURCE_ROOT, as run-tests.ps1 does)");
        var text = File.ReadAllText(Path.Combine(root, "src", "RemSound.App", "MainForm.cs"));
        var asks = CountOccurrences(text, "IsPeerConnectedNow(");
        Check(asks >= 3,
            $"the status readout and the connected-peers list must both ask the one rule (IsPeerConnectedNow is used {asks - 1} time(s))");
        Check(CountOccurrences(text, "PeerConnectionRule(") >= 3,
            "the connect and disconnect cues must decide by the same rule");
        Check(!text.Contains("ph.State != PeerHealthState.Healthy) continue;") && !text.Contains("ph is { State: PeerHealthState.Healthy }"),
            "nothing may decide \"connected\" by the heartbeat alone any more — that is the disagreement being fixed");
        return "audio or a healthy heartbeat means connected, a blip holds it, and the cue, the readout and the list all ask that one rule";
    }

    /// <summary>
    /// THE PEER DETAILS BOX HOLDS STILL WHILE IT IS BEING READ.
    ///
    /// <para>2026-09-13 review. The box's "Connected for" line changes every second, and it was rewritten every second
    /// whether or not it had focus — under NVDA while someone was reading it. Every other readout in the window follows
    /// ShouldWriteReadout, which is pinned on its own; this pins that the details box uses it.</para>
    /// </summary>
    private static string? AuditPeerDetailsHoldStillWhileRead()
    {
        var root = FindSourceRoot();
        if (root is null) return Skip("the source tree is not reachable from here (set REMSOUND_SOURCE_ROOT, as run-tests.ps1 does)");
        var writes = File.ReadAllLines(Path.Combine(root, "src", "RemSound.App", "MainForm.cs"))
            .Select((line, index) => (Text: line, Line: index + 1))
            .Where(l => l.Text.Contains("peerDetailsBox.Text = ", StringComparison.Ordinal))
            .ToList();
        Check(writes.Count > 0, "the peer details box must still be written somewhere — no write was found");
        var unguarded = writes.Where(w => !w.Text.Contains("ShouldWriteReadout(", StringComparison.Ordinal)).ToList();
        Check(unguarded.Count == 0,
            $"the peer details box is written without ShouldWriteReadout (line {string.Join(", ", unguarded.Select(u => u.Line))}) — "
            + "it changes every second, so it is rewritten under a screen reader while it is being read");
        return $"every write to the peer details box ({writes.Count}) holds still while the box has focus";
    }
}
