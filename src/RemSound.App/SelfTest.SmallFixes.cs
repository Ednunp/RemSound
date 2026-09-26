using System.Collections.Concurrent;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// Six small things from the full review, 2026-09-25 (Ed: "yes all 6"): people on a server called "(offline)" and counted
/// as one in the tray; the volume hotkeys neither saved nor logged; the start-up update question asked again at every
/// profile switch; remote system volume turning a device that is no longer the default; and three questions that can
/// come from the tray opening behind everything.
/// </summary>
internal static partial class SelfTest
{
    [DllImport("user32.dll", EntryPoint = "GetWindow")]
    private static extern IntPtr NativeGetWindow(IntPtr hWnd, uint uCmd);
    private const uint GW_OWNER = 4;

    /// <summary>A pong answering <paramref name="data"/>, if it is one of our pings; otherwise null.</summary>
    private static byte[]? PongTo(byte[] data, int length)
    {
        if (length < RemPacket.HeaderSize + RemPacket.HeartbeatPayloadSize) return null;
        if (!RemPacket.TryReadHeader(data.AsSpan(0, length), out var type, out _, out _) || type != RemPacketType.Heartbeat) return null;
        if (!RemPacket.TryReadHeartbeat(data.AsSpan(RemPacket.HeaderSize, length - RemPacket.HeaderSize), out var kind, out var tick)
            || kind != HeartbeatKind.Ping) return null;
        var pong = new byte[RemPacket.HeaderSize + RemPacket.HeartbeatPayloadSize];
        RemPacket.WriteHeader(pong, RemPacketType.Heartbeat, 0xFFFF, 1);
        RemPacket.WriteHeartbeatPayload(pong.AsSpan(RemPacket.HeaderSize), HeartbeatKind.Pong, tick);
        return pong;
    }

    /// <summary>
    /// EVERYBODY ON A SERVER IS THERE, AND COUNTED, WHILE THEY ANSWER - EACH WITH CUES OF THEIR OWN.
    ///
    /// <para>Somebody on a server is reached through it: our ping goes to the server, which passes it to each person who
    /// has ticked us and whom we have ticked, and each one's pong comes back as theirs. The connected list tagged everybody
    /// on a server "(offline)" unless they happened to be sending sound, and the tray counted three people there as one.
    /// Then (2026-09-25 sweep; Ed: per person) the list, the tag and the cues followed the server as a whole: ticking
    /// somebody there played no cue, their leaving played none, and somebody who had not ticked you back showed as
    /// connected whenever anybody else answered. Driven through the real window, each person's pong answering the
    /// window's own pings as the server delivers them.</para>
    /// </summary>
    private static string? EverybodyOnAServerIsThereWhileItAnswers()
    {
        var restoreMuted = CuePlayer.GloballyMuted;
        CuePlayer.GloballyMuted = true;
        var restoreMode = AppConfig.Load().AcceptPeerConnections;
        MainForm? form = null;
        HeartbeatService? hb = null;
        try
        {
            SetAcceptMode(PeerAcceptMode.Manual);
            try { form = new MainForm(null, Profile.NewBlank(), null, null, headless: true); }
            catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }

            var relay = new IPEndPoint(IPAddress.Parse("203.0.113.94"), RemPacket.DefaultPort);
            form.ConnectToRelayForTest("remote.example.test", relay);
            (Guid Id, string Name, bool TicksUs)[] people =
                [(Guid.NewGuid(), "ED_DT", true), (Guid.NewGuid(), "ANDRE_PC", true), (Guid.NewGuid(), "JONATHAN_LT", true)];
            Feed(form.RelayGroupForTest, relay, RosterPacket(people, paired: false), RelayInbound.Consumed, "a member list with three people on it");
            form.SyncRelayPeersListForTest();
            for (var i = 0; i < people.Length && form.RelayPeersListForTest.Items.Count > 0; i++)
            {
                form.RelayPeerTickedForTest(0, true);
                form.SyncAllPeerListsForTest();
                form.SyncRelayPeersListForTest();
            }
            Check(people.All(p => form.RelayTickedForTest.Contains(p.Id)), "premise: all three people on the server ticked");
            Check(form.SelectedSendEndpointsForTest().SequenceEqual([relay]), "premise: everybody on the server is reached at the server");

            // Everybody on the server answers every ping the window sends there, each as themselves - as the server
            // delivers them - unless they have gone quiet.
            var answering = 1;
            var quiet = new System.Collections.Concurrent.ConcurrentDictionary<IPAddress, bool>();
            var lines = new List<string>();
            form.LogForTest.EventTapForTest = line => { lock (lines) lines.Add(line); };
            bool Logged(string words) { lock (lines) return lines.Any(l => l.Contains(words, StringComparison.Ordinal)); }
            hb = form.StartHeartbeatForTest((data, length, to) =>
            {
                if (Volatile.Read(ref answering) == 1 && to.Equals(relay) && PongTo(data, length) is { } pong)
                {
                    foreach (var member in form.RelayGroupForTest.Members)
                    {
                        if (quiet.ContainsKey(member.Address.Address)) continue;
                        var from = member.Address;
                        ThreadPool.QueueUserWorkItem(_ => hb?.HandleInjectedPacket(pong, pong.Length, from));
                    }
                }
                return true;
            });
            // What connecting does (ApplyAudioRuntime); a headless window never starts its network.
            hb.SetTrackedPeers(form.SelectedSendEndpointsForTest());

            List<PeerListItem> Rows()
            {
                form.SyncAllPeerListsForTest();
                form.UpdateConnectedListLiveStatusForTest();
                return form.ConnectedPeersListForTest.Items.OfType<PeerListItem>().ToList();
            }
            string Names(List<PeerListItem> rows) => string.Join(", ", rows.Select(r => r.Peer.Name));

            Check(WaitFor(() => Rows() is { Count: 3 } r && r.All(x => x.Status.Connected), TimeSpan.FromSeconds(5)),
                $"premise: with the server answering, all three must be connected ({Names(Rows())})");
            var rows = Rows();
            Check(rows.All(r => !r.Peer.Name.Contains(" (offline)", StringComparison.Ordinal)),
                $"THE WRONG TAG: while the server answers, nobody on it may be called offline ({Names(rows)})");
            var details = form.PeerDetailsForTest(rows[0]);
            Check(!details.Contains("(offline)", StringComparison.Ordinal) && details.Contains("Link: healthy", StringComparison.Ordinal),
                $"and the details box must say so too ({details.Replace(Environment.NewLine, " | ")})");
            var tip = form.TrayTooltipForTest();
            Check(tip.Contains("3 peers", StringComparison.Ordinal),
                $"THE WRONG COUNT: the tray must count three people on a server as three, not as the server's one heartbeat (it says: {tip})");
            form.DetectPeerHealthTransitionsForTest();
            Check(people.All(p => Logged($"peer connected: {p.Name} on the server")),
                "THE SILENT TICK: each person on the server must get a connect cue of their own as they answer");

            // One of them goes quiet - leaves, or unticks us. Only they go, with a cue of their own; the others stay.
            var jonathan = form.RelayGroupForTest.Members.Single(m => m.Name == "JONATHAN_LT").Address.Address;
            quiet[jonathan] = true;
            Check(WaitFor(() =>
                {
                    var now = Rows();
                    form.DetectPeerHealthTransitionsForTest();
                    var him = now.Single(r => r.Peer.Name.StartsWith("JONATHAN_LT", StringComparison.Ordinal));
                    return him.Peer.Name.Contains(" (offline)", StringComparison.Ordinal) && !him.Status.Connected
                        && now.Where(r => r != him).All(r => r.Status.Connected);
                }, TimeSpan.FromSeconds(10)),
                $"THE SERVER'S WORD: one person going quiet must show as offline on their own, while the others stay connected ({Names(Rows())})");
            // The cue waits for five seconds of silence, as it does for anybody, where the tag goes at two.
            Check(WaitFor(() => { form.DetectPeerHealthTransitionsForTest(); return Logged("peer disconnected: JONATHAN_LT on the server"); }, TimeSpan.FromSeconds(10)),
                "THE SILENT LEAVING: somebody on the server going must get a disconnect cue of their own");
            Check(!Logged("peer disconnected: ED_DT on the server") && !Logged("peer disconnected: ANDRE_PC on the server"),
                "and nobody else's");
            quiet.Clear();

            // And the tag still works: once the server stops answering, they are gone by every measure.
            Volatile.Write(ref answering, 0);
            Check(WaitFor(() => Rows().All(r => r.Peer.Name.Contains(" (offline)", StringComparison.Ordinal)), TimeSpan.FromSeconds(8)),
                $"once the server stops answering, everybody on it must be tagged offline ({Names(Rows())})");
            Check(WaitFor(() => { Rows(); var t = form.TrayTooltipForTest(); return t.Contains("not connected", StringComparison.Ordinal) || t.Contains("no peers", StringComparison.Ordinal); },
                    TimeSpan.FromSeconds(10)),
                $"and the tray must count nobody ({form.TrayTooltipForTest()})");

            return "with everybody on the server answering, each is shown as there, in the list and the details box, each got a "
                 + "connect cue, and the tray counts three people as three; one going quiet went offline alone, with a disconnect "
                 + "cue of their own; when the server stops answering they are all tagged offline and the tray counts nobody";
        }
        finally
        {
            try { hb?.Dispose(); } catch { /* teardown */ }
            try { form?.Dispose(); } catch { /* teardown */ }
            try { SetAcceptMode(restoreMode); } catch { /* best effort */ }
            CuePlayer.GloballyMuted = restoreMuted;
        }
    }

    /// <summary>
    /// A VOLUME HOTKEY IS A CHANGE TO SAVE, AND IS LOGGED.
    ///
    /// <para>The listening-volume hotkeys set the slider directly, which does not raise its Scroll event, so a volume
    /// changed by hotkey was never offered for saving, never auto-saved and never written to the log. Pressed here exactly
    /// as the hotkey presses it, on a real profile store.</para>
    /// </summary>
    private static string? AVolumeHotkeyIsAChangeToSave()
    {
        var restoreMuted = CuePlayer.GloballyMuted;
        CuePlayer.GloballyMuted = true;
        var dir = Path.Combine(Path.GetTempPath(), "remsound-volumehotkey-" + Guid.NewGuid().ToString("N"));
        MainForm? form = null;
        try
        {
            var store = new ProfileStore(dir);
            var profile = Profile.NewBlank();
            profile.Title = "Volume profile";
            profile.Password = RemSoundCrypto.Obfuscate("a password for the gate");
            store.Save(profile);
            try { form = new MainForm(store, profile, profile.Title, null, headless: true); }
            catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }
            _ = form.Handle;
            Check(!form.UnsavedChangesForTest, "premise: a profile just loaded has nothing waiting to be saved");

            var said = new ConcurrentQueue<string>();
            form.LogForTest.EventTapForTest = said.Enqueue;
            var before = form.ListeningVolumeForTest;
            var up = before <= 50;
            if (up) form.VolumeUpFromHotkey(); else form.VolumeDownFromHotkey();
            Check(WaitFor(() => { Application.DoEvents(); return form.ListeningVolumeForTest != before; }, TimeSpan.FromSeconds(3)),
                $"the volume {(up ? "up" : "down")} hotkey must move the listening volume (still {before}%)");
            var after = form.ListeningVolumeForTest;
            Check(form.UnsavedChangesForTest,
                "THE LOST CHANGE: a volume changed by hotkey must count as a change to the profile, as the slider does");
            Check(said.Any(l => l.Contains($"ui: listening volume (hotkey) → {after}%", StringComparison.Ordinal)),
                $"THE SILENT LOG: the log must say the hotkey changed the listening volume (it said: {string.Join(" | ", said.TakeLast(3))})");
            form.LogForTest.EventTapForTest = null;

            form.RunAutoSaveTickForTest();
            var saved = store.Load(profile.Title);
            Check(saved?.Volume == after, $"and the auto-save must carry it to the file ({saved?.Volume}%, wanted {after}%)");
            return "a listening volume changed by hotkey counts as a profile change, is logged, and reaches the file through the auto-save";
        }
        finally
        {
            try { form?.Dispose(); } catch { /* teardown */ }
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
            CuePlayer.GloballyMuted = restoreMuted;
        }
    }

    /// <summary>
    /// THE START-UP UPDATE QUESTION IS ASKED ONCE A RUN, NOT AT EVERY PROFILE SWITCH.
    ///
    /// <para>A profile switch builds a new main window, and every one ran the start-up check, so a user who switched
    /// profile was asked "Update available - install now?" again each time. Now only the window RemSound starts with
    /// asks; the background check carries on as before.</para>
    /// </summary>
    private static string? TheStartupUpdateQuestionIsAskedOnceARun()
    {
        var restoreMuted = CuePlayer.GloballyMuted;
        var wasActive = Windowless.Active;
        try
        {
            // An ordinary copy with sounds on: the only kind that asks at all.
            CuePlayer.GloballyMuted = false;
            Windowless.SetActiveForTest(false);
            Check(MainForm.StartupUpdateCheckAllowed(true, coldStart: true),
                "premise: the window RemSound starts with, setting on, must still check - or this proves nothing");
            Check(!MainForm.StartupUpdateCheckAllowed(true, coldStart: false),
                "THE REPEAT: a window built for a profile switch must not ask about updates again");
            Check(!MainForm.StartupUpdateCheckAllowed(false, coldStart: true), "and with the setting off, nothing asks");
        }
        finally
        {
            CuePlayer.GloballyMuted = restoreMuted;
            Windowless.SetActiveForTest(wasActive);
        }

        var root = FindSourceRoot();
        if (root is null) return Skip("the rule holds; the source is not reachable to check the start-up uses it (set REMSOUND_SOURCE_ROOT)");
        var text = File.ReadAllText(Path.Combine(root, "src", "RemSound.App", "MainForm.cs"));
        var asked = text.IndexOf("StartupUpdateCheckAllowed(startupCfg.CheckForUpdatesOnStartup, coldStart)", StringComparison.Ordinal);
        Check(asked > 0 && !Regex.IsMatch(text, @"StartupUpdateCheckAllowed\(startupCfg\.CheckForUpdatesOnStartup\)"),
            "the start-up check must be asked with whether RemSound itself has just started");
        var cold = text.IndexOf("var coldStart = isFirstLaunch;", StringComparison.Ordinal);
        Check(cold > 0 && cold < asked, "and that must be the genuine first window of the run, worked out before the check");
        return "only the window RemSound starts with asks about updates at start-up; a window built for a profile switch does not";
    }

    /// <summary>
    /// REMOTE SYSTEM VOLUME FOLLOWS THE DEFAULT OUTPUT WHEN IT MOVES.
    ///
    /// <para>The remote "system volume" commands keep the default output they found first, for speed while a key is held,
    /// and let go of it only when a call failed. After the default moved to another device that was still plugged in,
    /// they kept turning the old one. Windows reporting a device change now drops it. Read only: nothing here changes a
    /// volume.</para>
    /// </summary>
    private static string? RemoteSystemVolumeFollowsTheDefaultOutput()
    {
        SystemVolumeHelper.ForgetDevice();
        if (SystemVolumeHelper.TryReadState() is null) return Skip("this machine has no default output to read the volume of");
        Check(SystemVolumeHelper.CachedDeviceIdForTest is not null, "premise: reading the volume keeps the device it found");

        var restoreMuted = CuePlayer.GloballyMuted;
        CuePlayer.GloballyMuted = true;
        MainForm? form = null;
        try
        {
            try { form = new MainForm(null, Profile.NewBlank(), null, null, headless: true); }
            catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }
            _ = form.Handle;
            var said = new ConcurrentQueue<string>();
            form.LogForTest.EventTapForTest = said.Enqueue;
            form.AudioEndpointsChangedForTest();
            form.LogForTest.EventTapForTest = null;
            Check(SystemVolumeHelper.CachedDeviceIdForTest is null,
                "THE OLD DEVICE: when Windows reports an audio device change, the device kept for remote volume must be let go");
            Check(said.Any(l => l.Contains("device-event: Windows reported an audio endpoint change", StringComparison.Ordinal)),
                "and the log must say what happened");
            Check(SystemVolumeHelper.TryReadState() is not null && SystemVolumeHelper.CachedDeviceIdForTest is not null,
                "and the next remote volume command must find the current default output again");
            return "a device change lets go of the output kept for remote system volume, so the next command finds the current default";
        }
        finally
        {
            try { form?.Dispose(); } catch { /* teardown */ }
            SystemVolumeHelper.ForgetDevice();
            CuePlayer.GloballyMuted = restoreMuted;
        }
    }

    /// <summary>
    /// QUESTIONS THAT CAN COME FROM THE TRAY OPEN IN FRONT.
    ///
    /// <para>With RemSound in the tray, a question owned by the hidden main window opens behind everything, where a
    /// screen reader never finds it. Three could: "Profile file no longer exists" (a Recent profile or the quick-switch
    /// hotkey), and the Save profile as box after "Save them?" from a profile switch or from Exit. Each is driven here in
    /// a headless copy, and must be owned by the top-most window ForegroundDialog makes, never the main window.</para>
    /// </summary>
    private static string? QuestionsFromTheTrayOpenInFront()
    {
        var restoreMuted = CuePlayer.GloballyMuted;
        CuePlayer.GloballyMuted = true;
        Windowless.InstallHook();
        Windowless.SetActiveForTest(true);
        var forms = new List<MainForm>();
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        try
        {
            var store = StoreWithOneProfileForTest();
            var other = Profile.NewBlank();
            other.Title = "Other profile";
            store.Save(other);
            var otherPath = store.PathFor("Other profile");
            var switchTo = Require(typeof(MainForm).GetMethod("SwitchToRecentProfile", flags, [typeof(string)]),
                "MainForm.SwitchToRecentProfile(string) not found");
            var markDirty = Require(typeof(MainForm).GetMethod("MarkProfileDirty", flags, Type.EmptyTypes), "MainForm.MarkProfileDirty not found");

            MainForm Open(bool blank, bool dirty)
            {
                MainForm form;
                try
                {
                    form = blank
                        ? new MainForm(store, Profile.NewBlank(), null, null, headless: true)
                        : new MainForm(store, store.Load("Audit profile") ?? Profile.NewBlank(), "Audit profile", null, headless: true);
                }
                catch (Exception ex) { throw new StepSkipped(MainWindowCouldNotBeBuilt(ex)); }
                forms.Add(form);
                _ = form.Handle;
                if (dirty)
                {
                    markDirty.Invoke(form, null);
                    Check(form.UnsavedChangesForTest, "premise: the profile must have unsaved changes");
                }
                return form;
            }

            // The dialog that came up, and whether it is in front (owned by ForegroundDialog's top-most window) or behind
            // (owned by the main window, hidden in the tray).
            (Form Dialog, string Where) Find(MainForm main, Func<Form, bool> which, string what)
            {
                Form? found = null;
                WaitFor(() => (found = main.Invoke(() => Application.OpenForms.Cast<Form>()
                    .FirstOrDefault(f => f != main && f.IsHandleCreated && which(f)))) is not null, TimeSpan.FromSeconds(5));
                Check(found is not null, $"{what} must come up");
                var where = main.Invoke(() =>
                {
                    var owner = NativeGetWindow(found!.Handle, GW_OWNER);
                    if (owner == main.Handle) return "behind, owned by the main window";
                    return Control.FromHandle(owner) is Form { TopMost: true, ShowInTaskbar: false } ? "in front" : $"owned by something else ({owner})";
                });
                return (found!, where);
            }
            static bool Asks(Form f, string words) => f is HeadlessQuestionForm q && q.Question.Contains(words, StringComparison.Ordinal);

            // 1. A Recent profile whose file has gone.
            var first = Open(blank: false, dirty: false);
            var missing = Path.Combine(Path.GetTempPath(), "remsound-gone-" + Guid.NewGuid().ToString("N") + ".remsound.json");
            DriveWhilePumping(first, () =>
            {
                first.BeginInvoke(() => switchTo.Invoke(first, [missing]));
                var (box, where) = Find(first, f => Asks(f, "Profile file no longer exists"), "the 'profile file no longer exists' box");
                Check(where == "in front", $"THE HIDDEN BOX: 'profile file no longer exists' must open in front ({where})");
                first.Invoke(() => box.DialogResult = DialogResult.OK);
            }, TimeSpan.FromSeconds(30));

            // 2. Switching from a blank, never-saved session, answering Yes: the name box.
            var second = Open(blank: true, dirty: true);
            var engine = new RemoteControlEngine(() => second);
            DriveWhilePumping(second, () =>
            {
                second.BeginInvoke(() => switchTo.Invoke(second, [otherPath]));
                Find(second, f => Asks(f, "Save them before switching"), "the unsaved-changes question on a switch");
                Check(second.Invoke(() => engine.Execute("answer Yes")).StartsWith("ok", StringComparison.Ordinal), "the question must be answerable");
                var (name, where) = Find(second, f => f.Text == "Save profile as", "the Save profile as box on a switch");
                Check(where == "in front", $"THE HIDDEN BOX: Save profile as, from a profile switch, must open in front ({where})");
                second.Invoke(() => name.DialogResult = DialogResult.Cancel);
            }, TimeSpan.FromSeconds(30));
            Check(!second.IsDisposed && second.NextProfileTitleToLoad is null, "cancelling the name must stay where you are");

            // 3. Exit from a blank, never-saved session, answering Yes: the same box.
            var third = Open(blank: true, dirty: true);
            var engine3 = new RemoteControlEngine(() => third);
            DriveWhilePumping(third, () =>
            {
                third.BeginInvoke(() => third.Close());
                Find(third, f => Asks(f, "Save them before exiting"), "the unsaved-changes question at Exit");
                Check(third.Invoke(() => engine3.Execute("answer Yes")).StartsWith("ok", StringComparison.Ordinal), "the question must be answerable");
                var (name, where) = Find(third, f => f.Text == "Save profile as", "the Save profile as box at Exit");
                Check(where == "in front", $"THE HIDDEN BOX: Save profile as, from Exit, must open in front ({where})");
                third.Invoke(() => name.DialogResult = DialogResult.Cancel);
            }, TimeSpan.FromSeconds(30));
            Check(!third.IsDisposed, "cancelling the name at Exit must keep RemSound open");

            return "'profile file no longer exists' and the Save profile as box - from a switch and from Exit - each open in front, "
                 + "owned by ForegroundDialog's top-most window rather than the hidden main window";
        }
        finally
        {
            foreach (var form in forms)
            {
                try
                {
                    if (form.IsDisposed) continue;
                    Require(typeof(MainForm).GetField("unsavedChanges", flags), "MainForm.unsavedChanges not found").SetValue(form, false);
                    form.Dispose();
                }
                catch { /* teardown */ }
            }
            Windowless.SetActiveForTest(false);
            CuePlayer.GloballyMuted = restoreMuted;
        }
    }
}
