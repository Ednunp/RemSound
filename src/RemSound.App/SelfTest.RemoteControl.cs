using System.IO.Pipes;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Windows.Forms;
using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// RemSound with no window, driven through its control channel (<c>--headless</c> and <c>--control</c>). Ed,
/// 2026-09-24: "a way of driving remsound entirely without a window like I think you can with reaper" - and the
/// first try of reading the window from outside popped the profile picker up on him while he was listening.
///
/// <para>These drive the REAL main window and real dialogs through the real pipe, and check the effect on the app,
/// not the reply text: a reply that says "ticked" about a box nobody ticked is exactly the lie a control channel must
/// never tell. They also check the two promises made to the person at the machine: nothing takes focus and nothing
/// appears on screen while the copy is headless.</para>
/// </summary>
internal static partial class SelfTest
{
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int index);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RemoteRect rect);
    [StructLayout(LayoutKind.Sequential)] private struct RemoteRect { public int Left, Top, Right, Bottom; }

    private static bool ForegroundIsThisProcess()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
        return pid == (uint)Environment.ProcessId;
    }

    /// <summary>Run <paramref name="script"/> on a worker while this (UI) thread keeps pumping, so a command that opens
    /// a modal dialog - whose loop then runs on this thread - can be answered by the script's next command, exactly as
    /// it is in a real headless run. Whatever the script leaves open is closed before this returns.</summary>
    private static void DriveWhilePumping(Form form, Action script, TimeSpan limit)
    {
        // Once the script has finished - passed or failed - anything it left open is closed, and keeps being closed
        // until this returns: a dialog that opened only after the script gave up on it (Windows' file picker can take
        // a very long time the first time it looks at network drives) would otherwise hold this thread for ever, the
        // whole gate with it. It did, once, on 2026-09-24.
        using var sweeping = new CancellationTokenSource();
        Task? sweeper = null;
        var task = Task.Run(() =>
        {
            try { script(); }
            finally
            {
                sweeper = Task.Run(() =>
                {
                    while (!sweeping.IsCancellationRequested)
                    {
                        try
                        {
                            foreach (var d in Windowless.NativeDialogs()) Windowless.PressNativeButton(d, 2);   // IDCANCEL
                            form.Invoke(() =>
                            {
                                foreach (var f in Application.OpenForms.Cast<Form>().Where(f => f != form && f.Modal).ToList())
                                    f.DialogResult = DialogResult.Cancel;
                            });
                        }
                        catch { /* teardown */ }
                        sweeping.Token.WaitHandle.WaitOne(200);
                    }
                });
            }
        });
        var deadline = DateTime.UtcNow + limit;
        while (!task.IsCompleted && DateTime.UtcNow < deadline)
        {
            Application.DoEvents();
            Thread.Sleep(5);
        }
        sweeping.Cancel();
        try { sweeper?.Wait(2000); } catch { /* teardown */ }
        Check(task.IsCompleted, $"the remote-control script did not finish within {limit.TotalSeconds:0} s");
        task.GetAwaiter().GetResult();
    }

    private const string SettingsProbePassword = "gate-settings-secret-4417";

    /// <summary>
    /// REMOTE CONTROL: the real main window, its menus, a dialog and a question, all driven through the real pipe by the
    /// names a screen reader reads - and each change proved on the app itself, and in its log.
    /// </summary>
    private static string? RemoteControlDrivesTheWholeApp()
    {
        var restoreMuted = CuePlayer.GloballyMuted;
        CuePlayer.GloballyMuted = true;
        Windowless.InstallHook();
        Windowless.SetActiveForTest(true);
        MainForm? form = null;
        RemoteControlServer? server = null;
        var pipe = "RemSound.control.selftest." + Guid.NewGuid().ToString("N")[..8];
        var logged = new List<string>();
        var summary = "";
        try
        {
            // With a password, so the settings command has one to keep hidden.
            var profile = Profile.NewBlank();
            profile.Password = RemSoundCrypto.Obfuscate(SettingsProbePassword);
            try { form = new MainForm(null, profile, null, null, headless: true); }
            catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }
            _ = form.Handle;
            var main = form;
            server = new RemoteControlServer(pipe, () => main, new RemoteControlEngine(() => main)) { Log = l => { lock (logged) logged.Add(l); } };
            server.Start();

            string Ask(string command)
            {
                if (Environment.GetEnvironmentVariable("REMSOUND_SELFTEST_TRACE") == "1") Console.Error.WriteLine($"    [trace {DateTime.Now:HH:mm:ss.fff}] remote control: {command}");
                var reply = RemoteControl.Send(command, pipe, out var reached, connectTimeoutMs: 5000);
                Check(reached, $"the control channel did not answer \"{command}\": {reply}");
                return reply;
            }
            T OnUi<T>(Func<T> read) => main.Invoke(read);
            var lockBox = FieldOf<CheckBox>(main, "lockPeerAddressesBox");
            Check(lockBox is not null, "the main window has no lockPeerAddressesBox field any more; point this test at another tick box");

            DriveWhilePumping(main, () =>
            {
                // 1. It lists the window by the names NVDA reads, with each control's state and its id.
                var list = Ask("list");
                Check(list.Contains("tick box \"Send my audio\" not ticked", StringComparison.Ordinal),
                    $"list must name the Send my audio tick box as a screen reader reads it, with its state (got: {Head(list)})");
                Check(list.Contains("tick list \"Connected peers\"", StringComparison.Ordinal) && list.Contains("id=connectedPeersList", StringComparison.Ordinal),
                    "list must name the Connected peers list without its (Alt+C) hint, and give its id");

                // 2. A tick box, changed by name: the app's own setting and its own log line must follow.
                var saidBefore = main.LogForTest.EventTapForTest;
                var appLog = new List<string>();
                main.LogForTest.EventTapForTest = l => { lock (appLog) appLog.Add(l); };
                var reply = Ask("set Lock to these exact peer addresses on");
                Check(reply.StartsWith("ok", StringComparison.Ordinal), $"set must succeed (got: {reply})");
                Check(OnUi(() => lockBox!.Checked), "THE LIE: the reply said ticked, and the tick box isn't");
                Check(OnUi(() => main.SettingsForTest.LoadLockPeerAddresses()), "and the setting the tick box governs must have followed, through the app's own handler");
                Check(appLog.Any(l => l.Contains("lock peer addresses", StringComparison.Ordinal)), "and the app must have logged the change, as it does for a person");
                Ask("set Lock to these exact peer addresses off");
                Check(!OnUi(() => lockBox!.Checked) && !OnUi(() => main.SettingsForTest.LoadLockPeerAddresses()), "and off again");
                main.LogForTest.EventTapForTest = saidBefore;

                // 3. A list, by item: the live receiver follows.
                var smoothnessBefore = OnUi(() => main.ReceiverForTest.SmoothnessValue);
                reply = Ask("select Buffer smoothness 6");
                Check(reply.StartsWith("ok", StringComparison.Ordinal), $"select must succeed (got: {reply})");
                Check(OnUi(() => main.ReceiverForTest.SmoothnessValue) != smoothnessBefore, "the live receiver's smoothness must follow the list");

                // 3b. A key press, for whatever only a keyboard reaches: Down Arrow moves the list's cursor one on.
                var smoothnessList = FieldOf<ListBox>(main, "smoothnessBox");
                Check(smoothnessList is not null, "the main window has no smoothnessBox field any more; point this at another list");
                var at = OnUi(() => smoothnessList!.SelectedIndex);
                var smoothnessAt = OnUi(() => main.ReceiverForTest.SmoothnessValue);
                reply = Ask("key Buffer smoothness down");
                Check(reply.StartsWith("ok", StringComparison.Ordinal), $"key must succeed (got: {reply})");
                Check(OnUi(() => smoothnessList!.SelectedIndex) == at + 1, $"Down Arrow must move the cursor one on (was {at}, now {OnUi(() => smoothnessList!.SelectedIndex)})");
                Check(OnUi(() => main.ReceiverForTest.SmoothnessValue) != smoothnessAt, "and the app must have acted on it, as it does for a real key");

                // 4. Two controls share a start: never a guess.
                reply = Ask("set Continuous auto-tune on");
                Check(reply.StartsWith("error", StringComparison.Ordinal) && reply.Contains("continuousTuneBox", StringComparison.Ordinal) && reply.Contains("continuousTuneAsioBox", StringComparison.Ordinal),
                    $"a name two controls share must be refused with both ids listed, never guessed (got: {Head(reply)})");
                Check(Ask("click A control that is not there").StartsWith("error: no control called", StringComparison.Ordinal), "and a name nothing has is refused");

                // 5. A button that opens a dialog: the reply says what is waiting, and the dialog is driven the same way.
                reply = Ask("click Add peer by IP");
                Check(reply.Contains("waiting for an answer", StringComparison.Ordinal) && reply.Contains("\"Add manual peer\"", StringComparison.Ordinal),
                    $"a command that opens a dialog must come back saying so, naming it (got: {Head(reply)})");
                var dialog = OnUi(() => Application.OpenForms.Cast<Form>().FirstOrDefault(f => f.Text == "Add manual peer"));
                Check(dialog is not null, "the dialog must really be open");
                Check(OnUi(() => dialog!.Left) <= Windowless.OffScreen.X / 2, "HEADLESS: the dialog must be off-screen");
                Check(!ForegroundIsThisProcess(), "HEADLESS: and it must not have taken focus from whatever the person is doing");
                Check((OnUi(() => GetWindowLong(dialog!.Handle, -20)) & 0x80) != 0, "HEADLESS: and it must be off the taskbar and Alt+Tab");
                Check(Ask("menu File > Save").StartsWith("error", StringComparison.Ordinal),
                    "while a dialog waits, the main window must be out of reach, as it is for a person");
                Check(Ask("set Peer IP address or hostname 192.0.2.77").StartsWith("ok", StringComparison.Ordinal), "the dialog's field must take text by name");
                reply = Ask("answer Cancel");
                Check(reply.StartsWith("ok", StringComparison.Ordinal), $"the dialog must be answerable (got: {reply})");
                Check(WaitFor(() => OnUi(() => dialog!.IsDisposed || !dialog.Visible), TimeSpan.FromSeconds(3)), "and Cancel must close it");
                Check(!OnUi(() => main.ConnectedPeersListForTest.Items.Cast<object>().Any(i => (i.ToString() ?? "").Contains("192.0.2.77", StringComparison.Ordinal))),
                    "and Cancel must mean cancel: the typed address must not have been added");

                // 6. A menu that opens a dialog, closed by its own button.
                Check(Ask("menus").Contains("Preferences", StringComparison.Ordinal), "menus must list every item");
                reply = Ask("menu Options > Preferences");
                Check(reply.Contains("\"Preferences\"", StringComparison.Ordinal), $"a menu item that opens a dialog must say so (got: {Head(reply)})");
                Check(Ask("list").Contains("tick box \"Start minimised to tray\"", StringComparison.Ordinal), "list must now describe the dialog, the window a person would be in");
                // 6b. An Alt key does what it does for a person: Alt+L on the General tab is "Clear remembered applications list
                // (Alt+L)", which asks first. The key used to arrive as a key going down only, and a control's Alt letter never
                // fired (2026-09-24). A button's Alt key presses it; a tick box's would need the keyboard, which a hidden window
                // cannot take - set and click are for those.
                reply = Ask("key Start minimised to tray alt+l");
                Check(reply.Contains("waiting for an answer", StringComparison.Ordinal) && Ask("windows").Contains("Clear the whole remembered applications list", StringComparison.Ordinal),
                    $"Alt+L in Preferences must press Clear remembered applications list, which asks first (got: {Head(reply)})");
                Check(Ask("answer No").StartsWith("ok", StringComparison.Ordinal), "and its question must be answerable");
                Check(Ask("answer Close").StartsWith("ok", StringComparison.Ordinal), "and it closes with its own button");
                // 6c. wait: until a window has gone, and a clear error when it does not come.
                reply = Ask("wait gone Preferences 5");
                Check(reply.StartsWith("ok", StringComparison.Ordinal), $"wait gone must see Preferences close (got: {reply})");
                Check(WaitFor(() => OnUi(() => !Application.OpenForms.Cast<Form>().Any(f => f.Text == "Preferences")), TimeSpan.FromSeconds(3)), "Preferences must be gone");
                reply = Ask("wait A window that never opens 0.5");
                Check(reply.StartsWith("error", StringComparison.Ordinal) && reply.Contains("did not appear", StringComparison.Ordinal), $"wait must say when what it waited for never came (got: {reply})");
                reply = Ask("wait Add manual peer 1");
                Check(reply.StartsWith("error", StringComparison.Ordinal), "and must not find a window that was open earlier and is closed now");

                // 7. A question, raised the way the app raises every one (AppMessageBox, through ForegroundDialog): while
                // headless it is asked silently - a Windows message box would ding however hidden it was - and it is
                // listed with its text and answers, and the answer given is the answer the app gets.
                DialogResult? answered = null;
                main.BeginInvoke(() => answered = ForegroundDialog.Show(o => AppMessageBox.Show(o, "Self-test question: keep going?", "RemSound",
                    MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question)));
                Check(WaitFor(() => OnUi(() => Application.OpenForms.OfType<HeadlessQuestionForm>().Any()), TimeSpan.FromSeconds(3)), "the question must have been asked");
                Check(Windowless.NativeDialogs().Count == 0, "THE DING: a headless question must not be a Windows message box, which plays a sound however well it is hidden");
                reply = Ask("windows");
                Check(reply.Contains("question: \"RemSound\" says \"Self-test question: keep going?\" - answers: \"Yes\", \"No\", \"Cancel\"", StringComparison.Ordinal),
                    $"a question must be listed with its text and its answers (got: {Head(reply)})");
                var question = OnUi(() => Application.OpenForms.OfType<HeadlessQuestionForm>().First());
                Check(OnUi(() => question.Left) <= Windowless.OffScreen.X / 2, "HEADLESS: the question must be off-screen");
                Check(!ForegroundIsThisProcess(), "HEADLESS: and must not have taken focus");
                Check(Ask("click Add peer by IP").StartsWith("error", StringComparison.Ordinal), "while a question waits, nothing else may be used");
                Check(Ask("answer No").StartsWith("ok", StringComparison.Ordinal), "the question must be answerable by its button's name");
                Check(WaitFor(() => answered is not null, TimeSpan.FromSeconds(3)), "and answering must close it");
                Check(answered == DialogResult.No, $"THE LIE: the app must get the answer that was given (No), and got {answered}");

                // 7a. A task dialog - own buttons, a heading - asked the same silent way. Shown directly it dinged, and the
                // channel could neither read it nor press its buttons, so it refused every command after it (2026-09-25).
                var keep = new TaskDialogButton("&Keep them");
                var discard = new TaskDialogButton("&Discard them");
                TaskDialogButton? chose = null;
                main.BeginInvoke(() => chose = AppTaskDialog.ShowDialog(main, new TaskDialogPage
                {
                    Caption = "RemSound", Heading = "Self-test heading", Text = "Self-test task dialog: keep them?",
                    Icon = TaskDialogIcon.Warning, Buttons = { keep, discard }, AllowCancel = true,
                }));
                Check(WaitFor(() => OnUi(() => Application.OpenForms.OfType<HeadlessQuestionForm>().Any()), TimeSpan.FromSeconds(3)), "the task dialog must have been asked");
                Check(Windowless.NativeDialogs().Count == 0, "THE DING: a headless task dialog must not be Windows' own, which plays a sound and which the channel cannot answer");
                reply = Ask("windows");
                Check(reply.Contains("Self-test heading", StringComparison.Ordinal) && reply.Contains("\"Keep them\"", StringComparison.Ordinal) && reply.Contains("\"Discard them\"", StringComparison.Ordinal),
                    $"a task dialog must be listed with its heading, its words and its own buttons (got: {Head(reply)})");
                Check(Ask("answer Discard them").StartsWith("ok", StringComparison.Ordinal), "and answered by its own button's name");
                Check(WaitFor(() => chose is not null, TimeSpan.FromSeconds(3)) && chose == discard, $"THE LIE: the app must get the button that was pressed (Discard them), and got {chose?.Text ?? "nothing"}");

                // 7b. Windows' own file picker (the app opens them for sounds, profiles, folders): typed into and answered.
                var pick = Path.Combine(Path.GetTempPath(), "remsound-pick-" + Guid.NewGuid().ToString("N")[..8] + ".wav");
                File.WriteAllBytes(pick, [0]);
                try
                {
                    DialogResult? picked = null;
                    string? chosen = null;
                    main.BeginInvoke(() =>
                    {
                        using var dialog = new OpenFileDialog { CheckFileExists = true, Title = "Self-test: choose a file" };
                        picked = dialog.ShowDialog(main);
                        chosen = dialog.FileName;
                    });
                    Check(WaitFor(() => Windowless.NativeDialogs().Any(d => d.Title.Contains("Self-test", StringComparison.Ordinal)), TimeSpan.FromSeconds(30)), "the file picker must have opened");
                    // Nobody presses Open the instant a picker appears: it is still filling itself in, and ignores an Open
                    // that comes that early. In the full gate run it came that early (2026-09-24).
                    Thread.Sleep(1500);
                    var picker = Windowless.NativeDialogs().First(d => d.Title.Contains("Self-test", StringComparison.Ordinal));
                    Check(GetWindowRect(picker.Handle, out var pr) && pr.Left <= Windowless.OffScreen.X / 2, "HEADLESS: the file picker must be off-screen");
                    Check(!ForegroundIsThisProcess(), "HEADLESS: and must not have taken focus");
                    Check(Ask($"type {pick}").StartsWith("ok", StringComparison.Ordinal), "type must fill in the picker's file name");
                    Check(Ask("answer Open").StartsWith("ok", StringComparison.Ordinal), "and answer must press its Open button");
                    if (!WaitFor(() => picked is not null, TimeSpan.FromSeconds(5)))
                        Check(false, $"and the picker must close (still open: {Head(Ask("windows"))}; buttons: {string.Join(", ", Windowless.NativeDialogs().SelectMany(d => d.Buttons).Select(b => b.Text + "=" + b.Id))})");
                    Check(picked == DialogResult.OK && string.Equals(chosen, pick, StringComparison.OrdinalIgnoreCase),
                        $"THE LIE: the app must get the file that was typed (got {picked}, \"{chosen}\")");
                }
                finally { try { File.Delete(pick); } catch { /* teardown */ } }

                // 7c. Silent unless a sound is being tested.
                Check(Ask("sounds") == "RemSound is silent", "a headless copy must be silent");
                Check(Ask("sounds on").StartsWith("ok", StringComparison.Ordinal) && !CuePlayer.GloballyMuted, "sounds on must let them play, for testing one");
                Check(Ask("sounds off").StartsWith("ok", StringComparison.Ordinal) && CuePlayer.GloballyMuted, "and sounds off must silence it again");

                // 8. Status reads back the lines a person would read.
                Check(Ask("status").Contains("Not connected to any peer", StringComparison.Ordinal), "status must return the connection status box");

                // 9. What a copy with no speakers, no screen reader and perhaps no log file did: its log, its cues, its speech.
                reply = Ask("log 200 lock peer addresses");
                // Real lines, stamped with the time - "nothing with ... in it" names the words too, and passed for them once.
                Check(System.Text.RegularExpressions.Regex.IsMatch(reply, @"^\d\d:\d\d:\d\d\.\d{3} .*lock peer addresses", System.Text.RegularExpressions.RegexOptions.Multiline),
                    $"log must return what the app logged, filtered by words (got: {Head(reply)})");
                var probeWav = Path.Combine(Path.GetTempPath(), "remsound-cueprobe-" + Guid.NewGuid().ToString("N")[..8] + ".wav");
                try
                {
                    File.WriteAllBytes(probeWav, MinimalWav());
                    OnUi(() => { new CuePlayer(probeWav).Play(); return 0; });
                    reply = Ask("cues 5");
                    Check(reply.Contains(Path.GetFileName(probeWav), StringComparison.Ordinal) && reply.Contains("(silent)", StringComparison.Ordinal),
                        $"cues must name the cue that fired, and say it was silent (got: {Head(reply)})");
                }
                finally { try { File.Delete(probeWav); } catch { /* teardown */ } }
                OnUi(() => ScreenReader.Speak("Self-test speech probe"));
                reply = Ask("speech 5");
                Check(reply.Contains("Self-test speech probe", StringComparison.Ordinal), $"speech must return what would have been said (got: {Head(reply)})");
                reply = Ask("settings");
                Check(reply.Contains("This computer's settings", StringComparison.Ordinal) && reply.Contains("The profile, as the window holds it now", StringComparison.Ordinal),
                    $"settings must show this computer's settings and the profile (got: {Head(reply)})");
                Check(reply.Contains("\"Password\": \"(hidden)\"", StringComparison.Ordinal), $"and the profile's password must show only as hidden (got: {Head(reply)})");
                Check(!reply.Contains(SettingsProbePassword, StringComparison.Ordinal) && !reply.Contains(RemSoundCrypto.Obfuscate(SettingsProbePassword), StringComparison.Ordinal),
                    "THE LEAK: settings must never show the password, plain or scrambled");
            }, TimeSpan.FromSeconds(90));

            // 9. Every command in the log, with what came of it.
            lock (logged)
            {
                Check(logged.Count >= 21, $"every command must be logged (logged {logged.Count})");
                Check(logged.Any(l => l.StartsWith("remote control: set Lock to these exact peer addresses on -> ok", StringComparison.Ordinal)),
                    "and each line must say what was asked and what came of it");
            }
            summary = "a real main window driven through the real pipe: a tick box and a list proved on the app and its log, an ambiguous "
                + "name refused, a dialog opened, filled in and cancelled, a menu dialog and a Windows question answered, an Alt key pressed, waits, the log, cues, speech and settings read back with the password hidden, the main window "
                + "out of reach while each waited, nothing on screen or in focus, and every command logged";
        }
        finally
        {
            server?.Dispose();
            try { form?.Dispose(); } catch { /* teardown */ }
            Windowless.SetActiveForTest(false);
            CuePlayer.GloballyMuted = restoreMuted;
        }
        return summary;
    }

    /// <summary>
    /// REMOTE CONTROL: a password is never read back, and never written to the log in the clear.
    /// </summary>
    private static string? RemoteControlNeverReadsAPasswordBack()
    {
        // RemSound's REAL password boxes, not a stand-in. Until 2026-09-24 this used a made-up box masked with
        // UseSystemPasswordChar, and passed, while the real ones - marked by their Tag and not masked at all, so a
        // screen reader can read them to their owner - were read out by list and get, and a set echoed the new password
        // into the log. A stand-in proves the rule the stand-in was built to, never the product.
        var (dialog, input) = ProfilePasswordDialog.Build("Gate profile", "hunter2-selftest");
        var storeDir = Path.Combine(AppConfig.UserDataDirectory, "pwmgr-" + Guid.NewGuid().ToString("N")[..8]);
        using (dialog)
        {
            _ = dialog.Handle;
            var engine = new RemoteControlEngine(() => dialog);
            using var server = new RemoteControlServer("RemSound.control.selftest.pw." + Guid.NewGuid().ToString("N")[..8], () => dialog, engine);
            var list = engine.Execute("list");
            Check(list.Contains("Password for profile Gate profile", StringComparison.Ordinal), $"the real password box must be listed by its name (got: {Head(list)})");
            Check(!list.Contains("hunter2", StringComparison.Ordinal), $"THE LEAK: list must never show a password (got: {Head(list)})");
            Check(!engine.Execute("get \"Password for profile Gate profile\"").Contains("hunter2", StringComparison.Ordinal), "THE LEAK: nor get");
            // Through the server, the way a command really arrives, so what the LOG gets is what is checked. The server
            // hands each command to the window's own thread, so it is sent from a worker while this thread pumps.
            string byName = "", keyed = "";
            DriveWhilePumping(dialog, () =>
            {
                byName = server.LoggedLineForTest("set \"Password for profile Gate profile\" another-secret-1");
                // key is not a set, so wording-based hiding never saw it: only knowing what the control IS can hide it.
                keyed = server.LoggedLineForTest("key \"Password for profile Gate profile\" x");
            }, TimeSpan.FromSeconds(20));
            Check(input.Text.Contains("another-secret-1", StringComparison.Ordinal), "set must still type a password in");
            Check(byName.Length > 0 && !byName.Contains("another-secret-1", StringComparison.Ordinal), $"THE LEAK: the log line must hide it (logged: {byName})");
            Check(keyed.Contains("(hidden)", StringComparison.Ordinal), $"THE LEAK: keys typed into a password box must not be logged either (logged: {keyed})");

            // A password with spaces, in quotes: all of it hidden, not only its last word (2026-09-25).
            // And a password command that cannot finish within the channel's wait - the window busy, as it is while a driver
            // opens - reaching the box by part of its name, so nothing in its wording says "password" (2026-09-25).
            string quoted = "", slow = "";
            DriveWhilePumping(dialog, () =>
            {
                quoted = server.LoggedLineForTest("set \"Password for profile Gate profile\" \"correct horse battery\"");
                // An ordinary command between, so nothing the password command before it left behind can hide this one.
                server.LoggedLineForTest("windows");
                dialog.BeginInvoke(() => Thread.Sleep(RemoteControl.CommandWait + TimeSpan.FromMilliseconds(700)));
                slow = server.LoggedLineForTest("set \"Gate profile\" slow-secret-7");
            }, TimeSpan.FromSeconds(30));
            Check(input.Text.Contains("slow-secret-7", StringComparison.Ordinal), "premise: the slow command must have reached the password box");
            Check(quoted.Length > 0 && !quoted.Contains("horse", StringComparison.Ordinal) && !quoted.Contains("correct", StringComparison.Ordinal),
                $"THE LEAK: a quoted password with spaces must be hidden whole (logged: {quoted})");
            Check(slow.Length > 0 && !slow.Contains("slow-secret-7", StringComparison.Ordinal),
                $"THE LEAK: a password command that outlasts the wait must still be hidden in the log (logged: {slow})");
        }

        // The password manager lists EVERY profile's password on one page.
        Directory.CreateDirectory(storeDir);
        try
        {
            var store = new ProfileStore(storeDir);
            var p = Profile.NewBlank();
            p.Title = "Gate one";
            p.Password = RemSoundCrypto.Obfuscate("manager-secret-xyz");
            store.Save(p);
            var (manager, _) = ProfilePasswordManagerDialog.Build(store);
            using (manager)
            {
                var engine = new RemoteControlEngine(() => manager);
                var list = engine.Execute("list");
                Check(list.Contains("Password for profile", StringComparison.Ordinal), $"the manager's box must be listed (got: {Head(list)})");
                Check(!list.Contains("manager-secret-xyz", StringComparison.Ordinal), $"THE LEAK: the passwords manager must never be read out (got: {Head(list)})");
            }
        }
        finally { try { Directory.Delete(storeDir, recursive: true); } catch { /* throwaway */ } }
        // Nothing listening: --control says so at once, not after its ten-second connect wait (the pipe namespace was
        // misspelt, so every pipe "existed"; 2026-09-25).
        var started = DateTime.UtcNow;
        var nobody = RemoteControl.Send("windows", "RemSound.control.nobody." + Guid.NewGuid().ToString("N")[..8], out var reachedNobody, connectTimeoutMs: 10000);
        var took = (DateTime.UtcNow - started).TotalSeconds;
        Check(!reachedNobody && took < 3, $"with nothing listening, --control must say so at once (took {took:0.0} s: {nobody})");

        return "RemSound's real password boxes - the profile password and the passwords manager - are listed by name and never read back "
             + "by list, get, set's reply or the log, whether named or addressed by field id, quoted with spaces, or slow to finish; keys "
             + "typed into one are hidden too; and --control with nobody listening says so at once";
    }

    /// <summary>
    /// HEADLESS: the tray is still the person's way in - its menu opens for them - and a headless copy never checks for
    /// updates at start-up, even one told "sounds on".
    /// </summary>
    private static string? HeadlessTrayMenuOpensAndNoUpdateCheck()
    {
        var restoreMuted = CuePlayer.GloballyMuted;
        Windowless.InstallHook();
        Windowless.SetActiveForTest(true);
        MainForm? form = null;
        var probes = new List<Form>();
        try
        {
            // 1. The rule. A window made while hiding goes off-screen; one made while the person has the tray menu open
            // (Ed, 2026-09-24: right-clicking the tray icon of a headless copy did nothing - its menu was put off-screen
            // like every other window) is left where it asked to be. Handles only: nothing is ever shown.
            Form Probe()
            {
                var f = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(140, 140), ShowInTaskbar = false };
                probes.Add(f);
                _ = f.Handle;
                return f;
            }
            Check(Probe().Left <= Windowless.OffScreen.X / 2, "while nobody has asked, a new window must be made off-screen");
            Windowless.PersonOpeningTrayMenu();
            Check(Windowless.TrayMenuOpen, "the person pressing on the tray icon must pause the hiding");
            var theirs = Probe();
            Check(theirs.Left == 140 && theirs.Top == 140, $"a window made while the tray menu is theirs must be where it asked to be (it is at {theirs.Left},{theirs.Top})");
            Windowless.TrayMenuClosed();
            Check(Probe().Left <= Windowless.OffScreen.X / 2, "and once the menu closes, windows go off-screen again");

            // 2. The wiring: a press on the REAL tray icon opens the gap, and its menu closing shuts it.
            try { form = new MainForm(null, Profile.NewBlank(), null, null, headless: true); }
            catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }
            _ = form.Handle;
            var tray = form.TrayControllerForTest;
            void Raise(object target, string method, EventArgs args) =>
                Require(target.GetType().GetMethod(method, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public,
                    null, new[] { args.GetType() }, null), $"{target.GetType().Name}.{method} not found").Invoke(target, new object[] { args });
            Raise(tray.TrayIconForTest, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, 0, 0, 0));
            Check(!Windowless.TrayMenuOpen, "a left press on the tray icon opens no menu, so it must not pause the hiding");
            Raise(tray.TrayIconForTest, "OnMouseDown", new MouseEventArgs(MouseButtons.Right, 1, 0, 0, 0));
            Check(Windowless.TrayMenuOpen, "a right press on the real tray icon - a mouse click, or the Applications key on it - must pause the hiding");
            Raise(tray.TrayMenuForTest, "OnClosed", new ToolStripDropDownClosedEventArgs(ToolStripDropDownCloseReason.ItemClicked));
            Check(!Windowless.TrayMenuOpen, "and the tray menu closing must end the pause");

            // 3. No start-up update check in a headless copy - asked on its own, not through the mute switch.
            CuePlayer.GloballyMuted = false;   // what "sounds on" does
            Check(!MainForm.StartupUpdateCheckAllowed(true), "a headless copy told sounds on must still not check for updates at start-up");
            Windowless.SetActiveForTest(false);
            Check(MainForm.StartupUpdateCheckAllowed(true), "an ordinary copy with the setting on must still check - or this proves nothing");
            CuePlayer.GloballyMuted = true;    // a --silent run
            Check(!MainForm.StartupUpdateCheckAllowed(true), "and a --silent one must not");
        }
        finally
        {
            foreach (var p in probes) try { p.Dispose(); } catch { /* teardown */ }
            try { form?.Dispose(); } catch { /* teardown */ }
            Windowless.SetActiveForTest(false);
            CuePlayer.GloballyMuted = restoreMuted;
        }
        return "while headless, windows go off-screen except while the person has the tray menu open - a right press on the real "
             + "tray icon opens that gap and the menu closing shuts it - and no headless copy checks for updates at start-up, sounds on or off";
    }

    /// <summary>
    /// HEADLESS: a window made while headless never appears and never takes focus, and handing over gives it back.
    /// </summary>
    private static string? HeadlessWindowsStayOutOfSightUntilHandedOver()
    {
        Windowless.InstallHook();
        Windowless.SetActiveForTest(true);
        var restoreSpeech = ScreenReader.Suppressed;
        var restoreMuted = CuePlayer.GloballyMuted;
        Form? form = null;
        Form? waiting = null;
        try
        {
            form = new Form { Text = "headless test window", StartPosition = FormStartPosition.CenterScreen, ShowInTaskbar = true };
            form.Controls.Add(new Button { Text = "A button" });
            form.Show();
            for (var i = 0; i < 20; i++) { Application.DoEvents(); Thread.Sleep(5); }
            Check(form.Left <= Windowless.OffScreen.X / 2 && form.Top <= Windowless.OffScreen.Y / 2,
                $"a window shown while headless must be off-screen, whatever position it asked for (it is at {form.Left},{form.Top})");
            Check(!ForegroundIsThisProcess(), "and it must not take focus");
            Check((GetWindowLong(form.Handle, -20) & 0x80) != 0, "and it must be off the taskbar and out of Alt+Tab");
            form.Activate();
            for (var i = 0; i < 10; i++) { Application.DoEvents(); Thread.Sleep(5); }
            Check(!ForegroundIsThisProcess(), "and asking for activation outright must be refused");

            // The audio-driver splash runs on a thread of its own, out of the hook's reach: it must not start at all while
            // headless. It came up on screen after every wake (2026-09-25).
            var splash = AsioLoadingSplash.StartIfAsioDriverName("Gate driver", "Self-test: this must never appear");
            try { Check(splash is null, "HEADLESS: the audio-driver splash must not start while headless - no hook can hide it"); }
            finally { splash?.Dismiss(); }
            // sounds on, to test a cue, must not bring back the start-up questions and notices: one of them opened the window.
            CuePlayer.GloballyMuted = false;
            Check(Windowless.NobodyToAsk, "HEADLESS: with sounds on, a headless copy must still count as having nobody to ask");
            CuePlayer.GloballyMuted = true;

            // A question already waiting when the person takes over (shown here without its own loop, to stay on this thread).
            waiting = new Form { Text = "headless waiting question", ShowInTaskbar = false, Size = new Size(200, 120) };
            waiting.Show();
            for (var i = 0; i < 10; i++) { Application.DoEvents(); Thread.Sleep(5); }
            Check(waiting.Left <= Windowless.OffScreen.X / 2, "premise: the waiting question starts off-screen");

            // Show RemSound in the tray: the window is the person's now.
            form.Hide();
            ScreenReader.Suppressed = true;
            Windowless.SilentLaunch = false;
            Windowless.HandOver(form);
            var waitingLeft = waiting.Left;
            waiting.Hide();
            Check(!Windowless.Hiding, "handing over must stop the hiding");
            Check((GetWindowLong(form.Handle, -20) & 0x80) == 0, "the handed-over window must be back on the taskbar and in Alt+Tab");
            Check(form.Left > Windowless.OffScreen.X / 2, "and back where it can be seen");
            Check(waitingLeft > Windowless.OffScreen.X / 2, "and a question already waiting must come where it can be seen too, or the window is blocked by something nobody can find");
            Check(!ScreenReader.Suppressed, "THE SILENCE: a person who took over must hear RemSound speak - the speak-status hotkey, deletions announced");
            Check(!CuePlayer.GloballyMuted, "and hear its sounds, when it was not started --silent");
        }
        finally
        {
            try { waiting?.Dispose(); } catch { /* teardown */ }
            try { form?.Dispose(); } catch { /* teardown */ }
            Windowless.SetActiveForTest(false);
            // Back to the run's own: silent, and not a word out loud.
            ScreenReader.Suppressed = restoreSpeech;
            CuePlayer.GloballyMuted = restoreMuted;
        }
        return "a window shown while headless stayed off-screen, off the taskbar and out of focus even when it asked, the driver splash "
             + "never started and sounds on brought no questions back; handed over, the window and a waiting question came where they "
             + "can be seen, and speech and sounds came back";
    }

    /// <summary>
    /// REMOTE CONTROL: the channel is this account's alone - nobody else on the PC, and nothing over the network.
    /// </summary>
    private static string? RemoteControlChannelIsThisAccountsAlone()
    {
        var pipe = "RemSound.control.selftest." + Guid.NewGuid().ToString("N")[..8];
        using var host = new Form();
        _ = host.Handle;
        using var server = new RemoteControlServer(pipe, () => host, new RemoteControlEngine(() => host));
        server.Start();
        PipeSecurity? acl = null;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (acl is null && DateTime.UtcNow < deadline)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
                client.Connect(500);
                acl = client.GetAccessControl();
            }
            catch (TimeoutException) { /* not up yet */ }
        }
        Check(acl is not null, "the channel never opened");
        var rules = acl!.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToList();
        var me = WindowsIdentity.GetCurrent().User!;
        var network = new SecurityIdentifier(WellKnownSidType.NetworkSid, null);
        Check(rules.Any(r => r.AccessControlType == AccessControlType.Deny && r.IdentityReference.Equals(network)),
            "THE HOLE: anything arriving over the network must be refused outright");
        var allowed = rules.Where(r => r.AccessControlType == AccessControlType.Allow).Select(r => r.IdentityReference).Distinct().ToList();
        Check(allowed.Count == 1 && allowed[0].Equals(me), $"THE HOLE: only this Windows account may open it (allowed: {string.Join(", ", allowed)})");
        return "the control channel admits only the account running RemSound and refuses the network outright";
    }

    private static T? FieldOf<T>(object owner, string field) where T : class =>
        owner.GetType().GetField(field, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)?.GetValue(owner) as T;

    private static bool WaitFor(Func<bool> condition, TimeSpan limit)
    {
        var deadline = DateTime.UtcNow + limit;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(20);
        }
        return condition();
    }

    private static string Head(string text) => text.Length <= 300 ? text.Replace("\n", " | ") : text[..300].Replace("\n", " | ") + "...";
}
