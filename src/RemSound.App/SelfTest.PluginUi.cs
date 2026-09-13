using System.Net;
using RemSound.Core;
using AudioPlugSharp;
using RemSound.Plugin;
using RemSound.Receiver;

namespace RemSound.App;

/// <summary>
/// The plugin's own window, and the two rate conversions either side of it.
///
/// <para>The window is the accessibility bet: it is an ordinary WinForms panel precisely so a screen
/// reader can read it, which most plugin windows cannot manage. That property is fragile in a way
/// that is invisible to anyone testing by eye — a list that rebuilds itself every second reads
/// perfectly in a screenshot and is unusable with NVDA, because every rebuild re-announces the list
/// and throws away where the user was in it.</para>
///
/// <para>The rate conversion is the other silent failure: a host at 44.1 kHz with no resampling
/// transposes everyone by a semitone and a bit. SonoBus ships with exactly that bug open. It would
/// arrive as "my friend sounds strange", which is a long way from "the sample rate is wrong".</para>
/// </summary>
internal static partial class SelfTest
{
    private static string? PluginWindowWiring()
    {
        using var panel = new PluginEditorPanel();

        var peers = new List<(string, string)> { ("192.168.1.50", "Andre"), ("192.168.1.51", "Chris") };
        panel.PeerSource = () => peers;
        var status = "Not connected.";
        panel.StatusSource = () => status;

        (bool Send, bool Receive, IReadOnlyList<string> Peers, bool All, float SendDb, float ReceiveDb)? lastJob = null;
        panel.JobChanged += (send, receive, chosen, all, sl, rl) => lastJob = (send, receive, chosen, all, sl, rl);

        // --- The peer list comes from the app, with NAMES -----------------------------------------
        panel.Refresh(fromTimer: false);
        Check(panel.PeerItemCountForTest == 2, $"the peer list must be filled from the app ({panel.PeerItemCountForTest} items)");
        Check(panel.PeerLabelsForTest.SequenceEqual(new[] { "Andre", "Chris" }),
            "peers must be shown by NAME — a list of IP addresses is no use to somebody choosing who to put on a track");

        // THE SCREEN-READER RULE: refreshing must not rebuild an unchanged list. A rebuild
        // re-announces every item and moves the user's place — the control would be unusable, and
        // nothing about it would look wrong to a sighted tester.
        panel.SelectPeerForTest(1);
        var rebuildsBefore = panel.PeerListRebuildsForTest;
        panel.Refresh(fromTimer: true);
        panel.Refresh(fromTimer: true);
        Check(panel.SelectedPeerIndexForTest == 1,
            "a refresh with an unchanged list must leave the user exactly where they were");
        // And it must not have REBUILT. Checking only where the selection landed cannot see this:
        // the rebuild restores the selection by address afterwards, so the index comes back to the
        // right row either way — forcing a rebuild on every refresh left this step green
        // (2026-08-24). The harm is the rebuild itself. NVDA re-announces a list every time it is
        // repopulated, and this refresh runs once a second.
        Check(panel.PeerListRebuildsForTest == rebuildsBefore,
            $"two refreshes over an unchanged list rebuilt it {panel.PeerListRebuildsForTest - rebuildsBefore} time(s). "
            + "Every rebuild makes a screen reader re-announce the whole list — once a second, the control is unusable, "
            + "and nothing about it looks wrong on screen");

        // --- Both switches start OFF ---------------------------------------------------------------
        // A plugin dropped on a track must do nothing until it is told to. The alternative is an
        // instance broadcasting a track the moment it is inserted, which for a screen-reader user is
        // an invisible surprise.
        Check(!panel.SendChecked && !panel.ReceiveChecked,
            "a fresh panel must have neither direction ticked - inserting a plugin must not start sending a track on its own");
        Check(!panel.PeerListEnabledForTest, "and the peer list must be disabled until receiving is ticked");
        Check(!panel.AllPeersChecked, "and it must not be taking all peers either");

        // --- Receiving, and SEVERAL people on one track --------------------------------------------
        panel.SetReceiveForTest(true);
        Check(lastJob is { Receive: true, Send: false }, "ticking receive must tell the engine to receive, and only to receive");
        Check(panel.PeerListEnabledForTest, "the peer list must be usable when receiving");

        panel.SetPeerCheckedForTest(1, true);
        Check(lastJob?.Peers.SequenceEqual(new[] { "192.168.1.51" }) == true,
            $"ticking a peer must put THAT peer on the track (got {string.Join(",", lastJob?.Peers ?? [])})");

        // The whole reason the list is checkable: three people talking while you work should be one
        // plugin instance, not three.
        panel.SetPeerCheckedForTest(0, true);
        Check(lastJob?.Peers.OrderBy(a => a).SequenceEqual(new[] { "192.168.1.50", "192.168.1.51" }) == true,
            $"a second tick must ADD, not replace - one track carrying two people is the point of the control "
            + $"(got {string.Join(",", lastJob?.Peers ?? [])})");

        // MOVING THE CURSOR MUST NOT CHOOSE ANYBODY. Arrowing down a list and finding you had put
        // somebody on your track by passing over them would be a trap, and an invisible one.
        var peersBeforeArrow = lastJob?.Peers.ToList() ?? [];
        panel.SelectPeerForTest(0);
        Check(lastJob?.Peers.OrderBy(a => a).SequenceEqual(peersBeforeArrow.OrderBy(a => a)) == true,
            "moving the selection must change nothing - it is a cursor, not a choice");

        panel.SetPeerCheckedForTest(0, false);
        Check(lastJob?.Peers.SequenceEqual(new[] { "192.168.1.51" }) == true, "and unticking must remove just that one");

        // --- A list that changes underneath the user -----------------------------------------------
        // Somebody joining shifts every position after them. Positions are therefore used for nothing
        // but the cursor: who is ON the track is held by address and must not move.
        peers.Insert(0, ("192.168.1.49", "Jonathan"));
        panel.Refresh(fromTimer: true);
        Check(panel.PeerItemCountForTest == 3, "a peer appearing in RemSound must appear here without reopening the plugin");
        Check(panel.PeerCheckedForTest(2) && !panel.PeerCheckedForTest(0) && !panel.PeerCheckedForTest(1),
            "a peer joining must not move who is on the track - the ticks follow the PERSON, not the row");
        Check(panel.SelectedPeerIndexForTest == 1,
            $"and the keyboard must stay on the same person, who is now row 1 (landed on {panel.SelectedPeerIndexForTest})");

        // Somebody LEAVING must not be forgotten. They are off the screen, not off the track: when
        // they come back the track picks them up again, which is what anybody who set it up once
        // expects. Reading the ticks off the control instead would silently drop them.
        peers.RemoveAll(p => p.Item1 == "192.168.1.51");
        panel.Refresh(fromTimer: true);
        Check(panel.CheckedPeersForTest.Contains("192.168.1.51"),
            "a peer who disconnects must stay on this track's list - dropping them would mean setting the track up again every reconnect");
        peers.Insert(2, ("192.168.1.51", "Chris"));
        panel.Refresh(fromTimer: true);
        Check(panel.PeerCheckedForTest(2), "...and must come back ticked when they return");

        // --- All peers ------------------------------------------------------------------------------
        panel.SetAllPeersForTest(true);
        Check(lastJob?.All == true, "the all-peers tick must reach the engine");
        Check(!panel.PeerListEnabledForTest,
            "and the individual list must be disabled while it is on - it is disabled rather than hidden so the tab order never shifts");
        panel.SetAllPeersForTest(false);
        Check(lastJob?.All == false && lastJob?.Peers.Contains("192.168.1.51") == true,
            "unticking all-peers must go back to exactly the people who were chosen before it");

        // --- AND BOTH DIRECTIONS AT ONCE, which is the whole point of two switches -------------------
        panel.SetSendForTest(true);
        Check(lastJob is { Send: true, Receive: true },
            "both directions must be able to run on ONE instance - that is what saves a track needing two plugins");
        Check(lastJob?.Peers.Count > 0, "and the chosen peers must survive switching sending on beside it");

        panel.SetReceiveForTest(false);
        Check(lastJob is { Send: true, Receive: false } && lastJob?.Peers.Count == 0,
            "unticking receive must release the peers - otherwise they stay mute in RemSound with the plugin no longer playing them");
        Check(!panel.PeerListEnabledForTest,
            "the peer list must be disabled, not hidden, when not receiving - hiding it would shift the tab order under a screen-reader user mid-session");

        // --- The levels are in DECIBELS -------------------------------------------------------------
        // "1 for unity is not intuitive for daw users" (Anthony Reyers, 2026-09-01). 0 dB is.
        Check(panel.LevelMinimumForTest <= -60m && panel.LevelMaximumForTest >= 12m,
            $"the level controls must run from a real off to some boost ({panel.LevelMinimumForTest}..{panel.LevelMaximumForTest})");
        panel.SetSendLevelForTest(-6f);
        Check(lastJob is not null && Math.Abs(lastJob.Value.SendDb + 6f) < 0.001f,
            $"the send level must reach the engine in dB (got {lastJob?.SendDb})");
        panel.SetReceiveLevelForTest(3f);
        Check(lastJob is not null && Math.Abs(lastJob.Value.ReceiveDb - 3f) < 0.001f,
            $"the receive level must reach the engine in dB (got {lastJob?.ReceiveDb})");

        // --- Active is the user's own bypass ------------------------------------------------------
        panel.SetReceiveForTest(true);
        Check(lastJob is { Receive: true }, "back to receiving");
        panel.SetActiveForTest(false);
        Check(lastJob is { Send: false, Receive: false } && lastJob?.Peers.Count == 0,
            "unticking Active must stop BOTH directions and hand the peers back to RemSound's speakers, not leave them playing nowhere");
        panel.SetActiveForTest(true);
        Check(lastJob is { Send: true, Receive: true } && lastJob?.Peers.Contains("192.168.1.51") == true,
            "re-ticking Active must restore exactly what was on before, peers included - it is a bypass, not a reset");

        // --- The status line must say what is true, including the awkward cases -------------------
        status = "Receiving Andre onto this track.";
        panel.Refresh(fromTimer: true);
        Check(panel.StatusTextForTest.Contains("Receiving Andre"), "the status line must show what the engine reports");

        return "peer list arrives from the app with names; an unchanged refresh never rebuilds; ticks and cursor follow the PERSON when "
             + "the list changes; a disconnected peer stays on the track and returns ticked; two people on one track; all-peers; "
             + "levels in dB; Active works as a bypass that returns everybody";
    }

    /// <summary>THE PROPERTIES A HOST NEEDS BEFORE IT WILL LOAD ANYTHING.
    ///
    /// <para>Three properties inherited from AudioPluginBase were never set, and each one failed in
    /// total silence with nothing a host could report (Anthony Reyers, 2026-08-16):</para>
    ///
    /// <para><b>Contact</b> is marshalled into the VST3 factory info. Null becomes a null char*, the
    /// copy dereferences it, and GetPluginFactory returns NULL — the host sees a file containing no
    /// plugins. That single missing line is why RemSound never appeared in any effects list.
    /// <b>HasUserInterface</b> defaults to false, and the controller then refuses to build a view, so
    /// the accessible window could never exist. <b>CacheLoadContext</b> defaults to false, so every
    /// instance reloaded the whole RemSound stack — which is what desynchronised the host's
    /// processor/controller pairing and crashed Reaper with two instances loaded.</para>
    ///
    /// <para>All three are one-liners, and none of them can be caught by testing behaviour: the plugin
    /// works perfectly in every in-process test with all three wrong. Only a check that they are SET
    /// catches them, which is why this test exists at the level it does.</para></summary>
    private static string? PluginHostContract()
    {
        var plugin = new RemSoundPlugin();
        try
        {
            // --- The factory info. All three strings are marshalled; any null takes the factory out.
            Check(!string.IsNullOrWhiteSpace(plugin.Contact),
                "Contact must be set - a null one makes GetPluginFactory return NULL and the plugin appears in no host at all");
            Check(!string.IsNullOrWhiteSpace(plugin.Company), "Company is marshalled into the same struct and must be set");
            Check(!string.IsNullOrWhiteSpace(plugin.Website), "Website likewise");
            Check(!string.IsNullOrWhiteSpace(plugin.PluginName), "the plugin must have a name to show in the effects list");

            // --- The window.
            Check(plugin.HasUserInterface,
                "HasUserInterface must be TRUE, or the host never asks for a view and the accessible window can never appear");

            // --- The load context.
            Check(plugin.CacheLoadContext,
                "CacheLoadContext must be TRUE, or every instance reloads the whole engine and a second plugin can crash the host");

            // --- The ports must exist BEFORE Initialize runs -------------------------------------
            // The host sizes port buffers on its own schedule, and it did so before Initialize had
            // created them. The ports were then replaced by unsized ones and every audio block threw,
            // so no input was ever read and send mode was silent in both directions.
            Check(plugin.InputPorts is { Length: > 0 },
                "the input port must exist on a freshly constructed plugin, before Initialize - the host sizes buffers before then");
            Check(plugin.OutputPorts is { Length: > 0 }, "...and the output port");

            // A stable id: a DAW keys saved projects off it.
            Check(plugin.PluginID != 0, "the plugin must have a stable id, or saved projects lose their RemSound instances");
            return $"factory info complete (contact, company, website), window enabled, load context cached, "
                 + $"{plugin.InputPorts.Length} in / {plugin.OutputPorts.Length} out present before Initialize";
        }
        finally { try { plugin.CloseForTest(); } catch { } }
    }

    /// <summary>Sending must PASS THE TRACK THROUGH, and the plugin's state must survive being saved.
    ///
    /// <para>The first build cleared the output while sending, reasoning that the track was already
    /// audible. Effects are in series: a plugin that outputs silence silences the track from that
    /// point on. And the window wrote only EditValue while the host saves ProcessValue, so nothing the
    /// user chose was ever persisted — the instance reverted to sending within seconds.</para></summary>
    private static string? PluginSendPassthroughAndState()
    {
        var plugin = new RemSoundPlugin { Host = new StubAudioHost() };
        try
        {
            plugin.SetMaxAudioBufferSize(512, EAudioBitsPerSample.Bits64);
            plugin.Initialize();

            // --- Send mode must pass the track through ------------------------------------------
            plugin.SetJobForTest(send: true, receive: false, peer: null);
            var input = (AudioIOPortManaged)plugin.InputPorts[0];
            var output = (AudioIOPortManaged)plugin.OutputPorts[0];
            var left = input.GetAudioBuffer(0);
            var right = input.GetAudioBuffer(1);
            Check(left.Length > 0, "the input port must have real buffers after the host has set its size");

            for (var i = 0; i < left.Length; i++) { left[i] = 0.5; right[i] = -0.5; }

            // The FIRST block after a block-size or rate change is deliberately silent: that is when
            // buffers are resized, and resizing on the audio thread otherwise means allocating in the
            // middle of somebody's take. Assert that too, so the silence stays a considered choice
            // rather than becoming an accident again.
            plugin.Process();
            var firstBlock = (AudioIOPortManaged)plugin.OutputPorts[0];
            var quiet = 0;
            for (var i = 0; i < Math.Min(64, firstBlock.GetAudioBuffer(0).Length); i++)
                if (Math.Abs(firstBlock.GetAudioBuffer(0)[i]) < 0.001) quiet++;
            Check(quiet > 32, "the first block after a format change is silent while buffers resize");

            // Refill (the port buffers are read each block) and run a steady-state block.
            left = input.GetAudioBuffer(0);
            right = input.GetAudioBuffer(1);
            for (var i = 0; i < left.Length; i++) { left[i] = 0.5; right[i] = -0.5; }
            plugin.Process();

            var outLeft = output.GetAudioBuffer(0);
            var outRight = output.GetAudioBuffer(1);
            var passed = 0;
            for (var i = 0; i < Math.Min(64, outLeft.Length); i++)
                if (Math.Abs(outLeft[i] - 0.5) < 0.001 && Math.Abs(outRight[i] + 0.5) < 0.001) passed++;
            Check(passed > 32,
                $"while SENDING the plugin must pass the track through - effects are in series, and clearing the output "
              + $"silences the track from that point on ({passed} of 64 samples came through)");

            // --- State: what the window sets must be what the host saves --------------------------
            var peer = IPAddress.Parse("192.168.1.50");
            plugin.PushJobToParametersForTest(send: false, receive: true, peer: peer);
            var receiveParam = plugin.Parameters.First(p => p.ID == "receive");
            Check(Math.Abs(receiveParam.ProcessValue - 1) < 0.001,
                $"the window must write ProcessValue, not just EditValue - ProcessValue is what the host saves, and writing "
              + $"only EditValue is why the plugin reverted within seconds (got {receiveParam.ProcessValue})");

            // --- A PUSH MUST NOT BE READ BACK WHILE IT IS HALF WRITTEN --------------------------
            // All three parameters raise PropertyChanged into ApplyParameters, and SetParameter writes
            // EditValue and ProcessValue, so one push used to re-enter ApplyParameters up to four
            // times mid-write. ApplyParameters prefers the remembered ADDRESS over the index, and the
            // address was assigned LAST — so it kept resolving to the person the user had just moved
            // away from and calling SetJob with them. That is why choosing a peer in the window could
            // leave the track playing the previous one (Anthony Reyers, 2026-08-29: "iPhone gave me
            // HOMESERV's sound and the other way around"). A first instance often escaped it, having
            // no previous peer to fall back to; later instances did not.
            //
            // Checked by watching what the instance's own state looks like AT THE MOMENT a parameter
            // changes: against the old order this sees the previous address, which is the bug itself.
            // Park on "send" first, so the push under test genuinely moves the job parameter and the
            // watcher below has something to fire on. (With no app running there are no known peers,
            // so the peer INDEX stays 0 either way and never raises an event of its own.)
            plugin.PushJobToParametersForTest(send: true, receive: false, peer: null);

            var second = IPAddress.Parse("192.168.1.51");
            // The decision is made first and the parameters are told afterwards, which is the real
            // order: the window applies the change, then mirrors it into the parameters.
            plugin.SetJobForTest(send: false, receive: true, peer: second);

            string? addressDuringPush = null;
            var jobParameter = plugin.Parameters.First(p => p.ID == "receive");
            void Watch(object? _, System.ComponentModel.PropertyChangedEventArgs __)
                => addressDuringPush ??= plugin.ChosenPeerForTest?.ToString() ?? "nothing";
            jobParameter.PropertyChanged += Watch;
            try { plugin.PushJobToParametersForTest(send: false, receive: true, peer: second); }
            finally { jobParameter.PropertyChanged -= Watch; }

            Check(addressDuringPush is not null,
                "the push must actually move the receive parameter, or this check is proving nothing");
            Check(addressDuringPush == "192.168.1.51",
                $"while a peer change is being written to the parameters, the instance must already know the NEW peer - "
              + $"anything reading it mid-push resolves to the person the user just left, and puts them back on the track "
              + $"(saw '{addressDuringPush ?? "nothing"}' where 192.168.1.51 was being set)");
            Check(plugin.SavedPeerAddressForTest == "192.168.1.51",
                $"and the push must finish on the peer it was given (got {plugin.SavedPeerAddressForTest ?? "nothing"})");

            // Back to the first peer, so the saved-state checks below read what they always did.
            plugin.SetJobForTest(send: false, receive: true, peer: peer);
            plugin.PushJobToParametersForTest(send: false, receive: true, peer: peer);

            var saved = plugin.SaveState();
            Check(saved is { Length: > 0 }, "the plugin must save state");

            // A fresh instance must come back on the SAME PERSON, by address rather than by position.
            var reloaded = new RemSoundPlugin { Host = new StubAudioHost() };
            try
            {
                reloaded.Initialize();
                reloaded.RestoreState(saved);
                Check(reloaded.SavedPeerAddressForTest == "192.168.1.50",
                    $"the peer must be restored by ADDRESS, not by position in a list - somebody joining or leaving "
                  + $"reshuffles that list and a restored project would land on a stranger (got {reloaded.SavedPeerAddressForTest ?? "nothing"})");
            }
            finally { try { reloaded.CloseForTest(); } catch { } }

            // A chunk from an older build carries no address and must still load rather than throwing
            // away the user's whole plugin instance.
            var older = new RemSoundPlugin { Host = new StubAudioHost() };
            try
            {
                older.Initialize();
                older.RestoreState(System.Text.Encoding.UTF8.GetBytes("<AudioPluginSaveState></AudioPluginSaveState>"));
                Check(older.SavedPeerAddressForTest is null, "an older state chunk must load with no peer rather than failing");
            }
            finally { try { older.CloseForTest(); } catch { } }

            return "sending passes the track through; the window writes the value the host actually saves; "
                 + "the peer is restored by address, and an older state chunk still loads";
        }
        finally { try { plugin.CloseForTest(); } catch { } }
    }

    /// <summary>THE PLUGIN FOLDER MUST STAND ON ITS OWN — load it the way a DAW does.
    ///
    /// <para><b>Why this exists.</b> v6.0 shipped a plugin folder that Reaper scanned and got nothing
    /// from. The .vst3 was there, the managed assembly was there, the runtimeconfig was there — the
    /// gate checked for all of those and passed. What was missing was every NuGet DEPENDENCY: a .NET
    /// library build does not copy them, so there was no NAudio and no Concentus, and the assembly
    /// failed the moment it touched a resampler. Reaper cached "no plugins here" and it never appeared
    /// in the effects list.</para>
    ///
    /// <para><b>The lesson, and why this test is shaped the way it is.</b> The old check listed the
    /// files somebody expected to find. This one asks the ASSEMBLY what it needs and then proves the
    /// folder can satisfy it — nothing is derived from what the author had in mind. It loads into its
    /// own context with ONLY the plugin folder to probe, so the app's own copies of NAudio and the
    /// rest cannot quietly stand in. That substitution is exactly what made the shipped folder look
    /// fine from inside the app.</para></summary>
    private static string? PluginFolderLoadsOnItsOwn()
    {
        var folder = PluginInstaller.SourceDirectory;
        Check(Directory.Exists(folder), $"this build has no plugin folder ({folder})");

        var assemblyPath = Path.Combine(folder, "RemSound.Plugin.dll");
        Check(File.Exists(assemblyPath), "the managed plugin assembly must be in the shipped folder");

        // Its own context, probing the plugin folder ALONE. isCollectible so the gate does not hold
        // the files open afterwards.
        var context = new System.Runtime.Loader.AssemblyLoadContext("remsound-plugin-folder-check", isCollectible: true);
        try
        {
            var resolver = new System.Runtime.Loader.AssemblyDependencyResolver(assemblyPath);
            var missing = new List<string>();
            context.Resolving += (ctx, name) =>
            {
                // Framework assemblies come from the runtime, exactly as they do inside a DAW.
                var beside = Path.Combine(folder, name.Name + ".dll");
                if (File.Exists(beside)) return ctx.LoadFromAssemblyPath(beside);
                var resolved = resolver.ResolveAssemblyToPath(name);
                if (resolved is not null && File.Exists(resolved)) return ctx.LoadFromAssemblyPath(resolved);
                missing.Add(name.Name ?? name.FullName);
                return null;
            };

            var assembly = context.LoadFromAssemblyPath(assemblyPath);
            var pluginType = assembly.GetType("RemSound.Plugin.RemSoundPlugin");
            Check(pluginType is not null, "the plugin type must be in the shipped assembly");

            // Walk what it actually references, rather than a list of what somebody expected.
            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                if (reference.Name is null) continue;
                // Anything the .NET runtime supplies is a host concern, not ours to ship.
                if (reference.Name.StartsWith("System.", StringComparison.Ordinal)
                    || reference.Name is "mscorlib" or "netstandard" or "WindowsBase"
                    || reference.Name.StartsWith("Microsoft.", StringComparison.Ordinal)) continue;
                var beside = Path.Combine(folder, reference.Name + ".dll");
                Check(File.Exists(beside),
                    $"the plugin references {reference.Name} but it is not in the shipped folder - a DAW has nothing else to load it from");
            }

            // The real proof: CONSTRUCT it. Reflection over references catches a missing file; only
            // running the constructor catches a dependency that resolves and then fails, and it is
            // the constructor that a DAW calls first.
            var instance = Activator.CreateInstance(pluginType!);
            Check(instance is not null, "the plugin must construct from the shipped folder alone");
            Check(missing.Count == 0,
                $"loading the plugin from its own folder must resolve everything it asks for - could not find: {string.Join(", ", missing.Distinct())}");

            // And it must reach the audio code, because that is where the missing pieces actually bit.
            // Initialize builds the ports, the parameters and the resamplers - the NAudio types whose
            // absence is what Reaper silently swallowed.
            var initialize = Require(pluginType!.GetMethod("Initialize"),
                "the plugin must expose Initialize, or a DAW can never start it");
            var hostProperty = Require(pluginType.GetProperty("Host"),
                "the plugin must expose Host, or the DAW cannot hand it one");
            hostProperty.SetValue(instance, new StubAudioHost());
            try { initialize.Invoke(instance, null); }
            catch (Exception ex)
            {
                var inner = ex.InnerException ?? ex;
                Check(false, $"the plugin must INITIALISE from the shipped folder, not just construct: {inner.GetType().Name}: {inner.Message}");
            }

            // Tidy up: close its link and log rather than leaving a socket open in the gate. The
            // LOOKUP must not be swallowed — a renamed CloseForTest would leak a socket per run and
            // nothing would say so. Only the invoke is best-effort, because teardown failing is not
            // this test's subject.
            var closeForTest = Require(pluginType.GetMethod("CloseForTest", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance),
                "the plugin must expose CloseForTest, or this test leaves its loopback socket open every run");
            try { closeForTest.Invoke(instance, null); }
            catch { }

            var shipped = Directory.GetFiles(folder, "*.dll").Length;
            return $"the plugin folder loads and initialises on its own: {shipped} assemblies, every referenced one present, "
                 + "constructed and initialised with only that folder to probe";
        }
        finally { try { context.Unload(); } catch { } }
    }

    /// <summary>The plugin's named parameters — the screen-reader route that does not depend on the
    /// window working inside a particular host.
    ///
    /// <para>The window is the intended way to work this plugin. But keyboard focus across a host's
    /// plugin frame is the one thing that cannot be proven outside a real DAW, and a host that gets it
    /// wrong leaves the window unreachable with no way back. Every DAW exposes a plain parameter list,
    /// and in Reaper with OSARA that list is keyboard-navigable and spoken — so the same three
    /// decisions are also parameters. This checks they are there, named so they make sense read aloud
    /// out of context, and that moving them actually changes what the instance does.</para></summary>
    private static string? PluginParameters()
    {
        var plugin = new RemSoundPlugin { Host = new StubAudioHost() };
        plugin.Initialize();

        var parameters = plugin.Parameters?.ToList() ?? [];
        Check(parameters.Count >= 8,
            $"EVERY decision the window offers must ALSO be a parameter, or a host with broken window focus leaves no way in ({parameters.Count} found)");

        foreach (var id in new[] { "send", "receive", "peer", "peerinclude", "allpeers", "sendlevel", "receivelevel", "active" })
        {
            var parameter = parameters.FirstOrDefault(p => p.ID == id);
            Check(parameter is not null, $"parameter '{id}' must exist");
            Check(!string.IsNullOrWhiteSpace(parameter!.Name), $"parameter '{id}' must have a name");
            // Read aloud, out of context, with no screen to look at. "Mode" tells somebody nothing.
            Check(parameter.Name.Length > 4 && parameter.Name.Any(char.IsLower),
                $"parameter '{id}' is named '{parameter.Name}' - it must read as plain English when spoken alone");
        }

        var send = parameters.First(p => p.ID == "send");
        var receive = parameters.First(p => p.ID == "receive");
        var peer = parameters.First(p => p.ID == "peer");
        var sendLevel = parameters.First(p => p.ID == "sendlevel");
        var receiveLevel = parameters.First(p => p.ID == "receivelevel");
        var active = parameters.First(p => p.ID == "active");

        // BOTH DIRECTIONS OFF. A plugin that started sending the moment it was inserted would put a
        // track on the network before the user had said anything, and for somebody working by screen
        // reader that is invisible until a peer mentions it.
        Check(Math.Abs(send.DefaultValue) < 0.001, "a fresh instance must NOT send until it is told to");
        Check(Math.Abs(receive.DefaultValue) < 0.001, "and must not receive - it cannot know whose audio you wanted");
        Check(Math.Abs(active.DefaultValue - 1) < 0.001, "but it must be active, or the two switches would appear to do nothing");
        Check(Math.Abs(peer.DefaultValue) < 0.001, "with no peer chosen");
        Check(Math.Abs(sendLevel.DefaultValue) < 0.001, "and both levels at 0 dB, so switching a direction on changes nothing else");
        Check(Math.Abs(receiveLevel.DefaultValue) < 0.001, "...the receive level too");
        Check(plugin.SendGainForTest is 1f && plugin.ReceiveGainForTest is 1f,
            "and the engine must agree with those defaults - 0 dB is a multiply of exactly 1");

        // Moving a parameter must reach the engine. With no app running there are no known peers, so
        // the peer choice can only resolve to nobody - which is the point of the next check.
        receive.EditValue = 1;
        plugin.ApplyParameters();
        Check(plugin.ReceiveEnabled, "setting 'receive' must switch receiving on");
        Check(!plugin.SendEnabled, "...without switching sending on behind it");

        send.EditValue = 1;
        plugin.ApplyParameters();
        Check(plugin.SendEnabled && plugin.ReceiveEnabled,
            "the two switches are INDEPENDENT - one instance must be able to do both, which is the whole reason they are two");

        sendLevel.EditValue = -12;
        receiveLevel.EditValue = 6;
        plugin.ApplyParameters();
        Check(Math.Abs(plugin.SendGainForTest - 0.2512f) < 0.001f,
            $"the send level must reach the engine as a multiply (-12 dB is 0,251; got {plugin.SendGainForTest:0.0000})");
        Check(Math.Abs(plugin.ReceiveGainForTest - 1.9953f) < 0.001f,
            $"the receive level must reach the engine as a multiply (+6 dB is 1,995; got {plugin.ReceiveGainForTest:0.0000})");

        peer.EditValue = 5;                 // a peer that isn't there
        plugin.ApplyParameters();
        Check(plugin.ChosenPeerForTest is null,
            "a peer index past the end of the list must resolve to NOBODY - wrapping round would put a stranger on the track when somebody disconnects");

        active.EditValue = 0;
        plugin.ApplyParameters();
        Check(!plugin.SendEnabled && !plugin.ReceiveEnabled,
            "unticking Active must stop BOTH directions and release the peer, exactly as the window's Active box does");

        plugin.Stop();   // releases the peer and closes the link, as a host deactivating the instance does
        plugin.CloseForTest();   // ...and drop its log, so it is not still ticking during the logging test
        return $"{parameters.Count} named parameters; both directions default OFF, active on, levels at 0 dB; "
             + "each direction and each level reaches the engine, the two directions are independent, and an out-of-range "
             + "peer resolves to nobody rather than wrapping onto somebody else";
    }


    /// <summary>A stand-in for the DAW, so the plugin can be initialised and driven by the gate.
    /// It answers the handful of things a host is asked at startup and does nothing else — the point
    /// is to exercise the REAL plugin, not to simulate Reaper.</summary>
    private sealed class StubAudioHost : IAudioHost
    {
        public double SampleRate => 48000;
        public uint MaxAudioBufferSize => 4096;
        public uint CurrentAudioBufferSize => 512;
        public EAudioBitsPerSample BitsPerSample => EAudioBitsPerSample.Bits64;
        public double BPM => 120;
        public long CurrentProjectSample => 0;
        public bool IsPlaying => false;
        public void ProcessAllEvents() { }
        public int ProcessEvents() => 0;
        public void SendNoteOn(int channel, int noteNumber, float velocity, int sampleOffset) { }
        public void SendNoteOff(int channel, int noteNumber, float velocity, int sampleOffset) { }
        public void SendPolyPressure(int channel, int noteNumber, float pressure, int sampleOffset) { }
        public void SendCC(int channel, int ccNumber, int ccValue, int sampleOffset) { }
        public void BeginEdit(int parameter) { }
        public void PerformEdit(int parameter, double normalizedValue) { }
        public void EndEdit(int parameter) { }
        public void SetParameter(int parameter, double normalizedValue) { }
        public void Log(string message) { }
    }

    /// <summary>The plugin's logging — off by default, honest when on, and never touching the audio
    /// thread.
    ///
    /// <para>This exists so a tester can send back evidence instead of an impression. Two things have
    /// to hold for that to be worth anything. It must write NOTHING when the user has logs switched
    /// off — a plugin that quietly creates files in somebody's session is its own bug. And when it is
    /// on, the numbers have to be real: a log full of zeros that were never wired up is worse than no
    /// log, because it looks like an answer.</para></summary>
    private static string? PluginLogging()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "remsound-plugin-log-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (AppConfig.UseThrowawayUserDataDirectory(scratch))
            {
                // --- OFF: nothing at all --------------------------------------------------------
                var off = AppConfig.Load();
                off.LoggingEnabled = false;
                off.Save();
                AppConfig.WritePluginPointer(loggingEnabled: false);

                using (var quiet = new PluginLog(Guid.NewGuid()))
                {
                    quiet.SnapshotSource = () => SampleSnapshot();
                    quiet.Event("this must never be written");
                    Check(quiet.Path is null, "with logs OFF the plugin must not create a file at all - not even an empty one");
                }
                Check(!Directory.Exists(AppConfig.LogsDirectory) || Directory.GetFiles(AppConfig.LogsDirectory, "RemSoundPlugin-*").Length == 0,
                    "with logs OFF there must be no plugin log file on disk");

                // --- ON: a real file, with real lines --------------------------------------------
                var on = AppConfig.Load();
                on.LoggingEnabled = true;
                on.Save();
                // The plugin reads the POINTER file, not AppConfig's own paths - inside a DAW those
                // point at the DAW's folder. Written here exactly as the app writes it at startup.
                AppConfig.WritePluginPointer(loggingEnabled: true);

                // --- The pointer file: how the plugin finds RemSound at all -------------------------
                // Inside a DAW the plugin cannot work out where RemSound keeps its logs - everything hangs
                // off the running program's own folder, which is the DAW's. Without this pointer the first
                // build wrote no plugin log anywhere, so the one file a tester was asked to send did not
                // exist.
                var pointer = AppConfig.ReadPluginPointer();
                Check(pointer is not null, "the app must record where it lives, or a plugin in a DAW can never find its logs folder");
                Check(Directory.Exists(pointer!.Value.UserDataDirectory), "...and it must point at a folder that is really there");


                string? path;
                using (var live = new PluginLog(Guid.NewGuid()))
                {
                    live.SnapshotSource = () => SampleSnapshot();
                    live.Event("initialised - host says 48000 Hz");
                    live.Event("job: receiving 192.168.1.50 onto this track");
                    path = live.Path;
                    Check(path is not null, "with logs ON the plugin must create a file");
                    // Wait on the COUNTER, not on the file. The per-second line is written by a
                    // timer, and reading a log while its own writer still holds it open is a
                    // file-sharing argument that proves nothing about whether the logging works.
                    Check(WaitUntil(() => live.SnapshotsWritten > 0, timeoutMs: 4000),
                        "the per-second snapshot line must actually be written");
                }

                var text = ReadSharedText(path!);
                Check(text.StartsWith("Kind\tTimestamp"), "the file must start with its column header, or nobody can read it");
                Check(text.Contains("plugin log started"), "it must say when it started, and in which host process");
                Check(text.Contains("job: receiving 192.168.1.50"), "events must be written verbatim");
                // The numbers a tester's report hinges on. If these are absent the log looks fine and
                // answers nothing.
                foreach (var column in new[] { "HostRate", "BlockFrames", "Resampling", "ShortBlocks", "RingFrames" })
                    Check(text.Contains(column), $"the snapshot must carry '{column}' - it is one of the numbers that explains a fault");

                var snap = text.Split('\n').First(l => l.StartsWith("SNAP"));
                Check(snap.Contains("\t44100\t") && snap.Contains("\tyes\t"),
                    $"the snapshot must report the host's real rate and whether we are resampling (got: {snap.Trim()})");
            }

            // --- The APP's half: every claim and release must reach the log ---------------------
            var written = new List<string>();
            var peer = IPAddress.Parse("192.168.1.50");
            using (var host = new PluginBridgeHost(null, port: 0))
            {
                host.Notable += written.Add;
                using (var plugin = new PluginBridgeClient(host.Port))
                {
                    plugin.Hello();
                    Check(WaitUntil(() => written.Any(l => l.Contains("said hello"))), "a plugin connecting must be logged");
                    plugin.ReceiveFrom(peer);
                    var block = new float[512];
                    plugin.ReadPeerBlock(block, 256);
                    Check(WaitUntil(() => written.Any(l => l.Contains("took 192.168.1.50"))),
                        "a plugin taking a peer must be logged - it is the moment that peer leaves the speakers");
                    plugin.ReceiveFrom(null);
                    Check(WaitUntil(() => written.Any(l => l.Contains("let 192.168.1.50 go"))),
                        "and letting them go must be logged, so a silent peer can be explained from a file");
                }
                Check(WaitUntil(() => written.Any(l => l.Contains("closed cleanly"))), "a plugin closing must be logged");
                Check(host.DescribeForLog().Contains("instances="), "the per-second summary must describe the link");
            }

            return $"off means no file at all; on gives a header, plain-English events and a per-second line carrying the host rate, "
                 + $"block size, resampling, short blocks and ring depth; the app logs {written.Count} link events including every claim and release";
        }
        finally
        {
            try { if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true); } catch { }
        }
    }

    /// <summary>The APP's plugin logging, through the real MainForm — does a plugin connecting
    /// actually end up in RemSound's log file?
    ///
    /// <para>The test above proves the bridge RAISES those events. That is not the same claim. The
    /// wiring from the bridge to the log file lives in the window, and if it were missing everything
    /// else would still pass while a tester's log came back empty of the one thing being tested. That
    /// is the exact shape of gap that has bitten this project before, so it is checked end to end:
    /// open the link the way the app opens it, connect a real plugin client, read the file back.</para></summary>
    private static string? PluginLoggingInTheApp()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "remsound-applog-" + Guid.NewGuid().ToString("N"));
        MainForm? form = null;
        try
        {
            using (AppConfig.UseThrowawayUserDataDirectory(scratch))
            {
                var cfg = AppConfig.Load();
                cfg.LoggingEnabled = true;
                cfg.EnableDawPluginLink = true;
                cfg.Save();

                var profile = Profile.NewBlank();
                profile.Password = RemSoundCrypto.Obfuscate("plugin-log-test-password");
                try { form = new MainForm(null, profile, null, null, headless: true); }
                catch (Exception ex) { return Skip($"headless MainForm could not be constructed: {ex.GetType().Name}: {ex.Message}"); }

                // Port 0, never 47831: a gate run must never fight the RemSound the user has open.
                var host = form.OpenPluginLinkForTest(0);
                Check(host is not null, "with the setting on, the app must open the plugin link");
                Check(host!.Port > 0, "and bind a port");

                using (var plugin = new PluginBridgeClient(host.Port))
                {
                    plugin.Hello();
                    Check(WaitUntil(() => host.InstanceCount > 0), "the app must see the plugin connect");
                    plugin.ReceiveFrom(IPAddress.Parse("192.168.1.50"));
                    var block = new float[512];
                    plugin.ReadPeerBlock(block, 256);
                    Check(WaitUntil(() => host.Claims.IsClaimed(IPAddress.Parse("192.168.1.50"))), "and see the claim");
                    form.WritePluginLogLineForTest();
                }

                var path = form.LogPathForTest;
                Check(path is not null, "with logging on, the app must have a log file");
                var text = ReadSharedText(path!);
                Check(text.Contains("vst plugin: link open"), "the app's log must record the link opening, with its port");
                Check(text.Contains("said hello"),
                    "the app's log must record a plugin connecting - this is the wiring the bridge's own test cannot prove");
                Check(text.Contains("took 192.168.1.50"),
                    "and record the peer being taken, which is the moment they leave the speakers");
                Check(text.Contains("instances=1"), "and carry the per-second summary");

                return "the app opens the link, and a plugin connecting, claiming a peer and the running summary all reach RemSound's own log file";
            }
        }
        finally
        {
            try { form?.Dispose(); } catch { }
            try { if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true); } catch { }
        }
    }

    /// <summary>Read a log the writer still has open. Plain File.ReadAllText asks for exclusive-ish
    /// sharing and fails against a live writer - which is exactly the state a tester is in when they
    /// go to send the file, so it is worth reading it the same way they would have to.</summary>
    private static string ReadSharedText(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static PluginLogSnapshot SampleSnapshot() => new(
        Job: "receive", Peer: "192.168.1.50", Connected: true,
        HostSampleRate: 44100, BlockFrames: 512, Resampling: true,
        BlocksOut: 0, BlocksIn: 120, ShortBlocks: 3, RingFrames: 558,
        BytesOut: 0, BytesIn: 245760, KnownPeers: 2);

    /// <summary>The plugin has to actually BE in this copy of RemSound, and be complete.
    ///
    /// <para>The install menu copies a <c>plugin\</c> folder next to the exe into the user's own VST3
    /// folder. If the build ever stops producing that folder, every other plugin test still passes —
    /// they drive throwaway directories — and the shipped app quietly reports that it has no plugin to
    /// install. That is precisely the class of gap Ed has been caught by before: built is not shipped.</para>
    ///
    /// <para>The named files are the ones whose absence is silent. A .vst3 with no
    /// <c>.runtimeconfig.json</c> beside it does not fail loudly — the DAW just doesn't list it, and
    /// the user is left believing the install did nothing.</para></summary>
    private static string? PluginPayloadPresent()
    {
        var folder = PluginInstaller.SourceDirectory;
        Check(Directory.Exists(folder),
            $"this build has no plugin folder ({folder}) - 'Install plugin' would find nothing to install");

        var files = Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetFileName(f)).ToList();

        Check(files.Any(f => f.EndsWith(".vst3", StringComparison.OrdinalIgnoreCase)),
            "there must be a .vst3 in the plugin folder - that IS the plugin");
        Check(files.Any(f => f.Equals("RemSound.Plugin.dll", StringComparison.OrdinalIgnoreCase)),
            "the managed plugin assembly must ship - the .vst3 is only a loader for it");
        Check(files.Any(f => f.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase)),
            "the runtimeconfig must ship beside the .vst3 - without it the DAW simply never lists the plugin, with no error to explain why");
        Check(files.Any(f => f.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase)),
            "the deps.json must ship - it is how the loader finds the managed assemblies");
        Check(files.Any(f => f.Equals("Ijwhost.dll", StringComparison.OrdinalIgnoreCase)),
            "Ijwhost.dll must ship - it is the shim that starts the .NET runtime inside the DAW");
        Check(files.Any(f => f.Equals("RemSound.Ui.dll", StringComparison.OrdinalIgnoreCase)),
            "the shared accessible controls must ship - they are the entire reason the plugin window can be read by a screen reader");

        // And the real installer must succeed FROM this real folder, into a throwaway target. Testing
        // the installer only against invented files would never catch a payload that cannot be copied.
        var target = Path.Combine(Path.GetTempPath(), "remsound-plugin-payload-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (ok, message) = PluginInstaller.InstallForTest(folder, target);
            Check(ok, $"installing the REAL plugin folder must succeed: {message}");
            Check(PluginInstaller.IsInstalledAt(target), "...and be detected as installed afterwards");
            var placed = Directory.GetFiles(target, "*", SearchOption.AllDirectories).Length;
            Check(placed >= files.Count, $"every file must arrive ({placed} placed, {files.Count} in the folder plus its manifest)");
        }
        finally
        {
            try { if (Directory.Exists(target)) Directory.Delete(target, recursive: true); } catch { }
        }

        return $"the plugin ships with this build ({files.Count} files incl. the .vst3, its runtimeconfig, deps, Ijwhost and the accessible controls) "
             + "and the real installer copies it whole";
    }

    /// <summary>Both rate conversions, driven with a real tone and measured — because "it ran" is not
    /// evidence that a resampler is correct, and the failure it guards against (everyone a semitone
    /// sharp) arrives as "my friend sounds strange", not as an error.</summary>
    private static string? PluginRateConversion()
    {
        const double HostRate = 44100.0;
        const double ToneHz = 1000.0;
        const int HostBlock = 512;

        // --- Out of the DAW: host rate -> 48k wire -------------------------------------------------
        var captured = new List<float>();
        var capture = new HostCaptureBackend(samples => captured.AddRange(samples.Span.ToArray()));
        capture.PrepareForBlockSize(HostBlock, HostRate);
        capture.Start([]);

        var left = new double[HostBlock];
        var right = new double[HostBlock];
        var phase = 0.0;
        var step = 2 * Math.PI * ToneHz / HostRate;
        for (var block = 0; block < 200; block++)
        {
            for (var i = 0; i < HostBlock; i++) { left[i] = right[i] = Math.Sin(phase); phase += step; }
            capture.SubmitHostBlock(left, right);
        }
        capture.Stop();

        Check(captured.Count > 0, "the DAW's block must reach the wire at all");
        // A 44.1k host produces MORE 48k frames than it consumed. If this came out equal, the
        // resampler was skipped and the far end would hear everyone sharp.
        var wireFrames = captured.Count / 2;
        var expectedWire = 200 * HostBlock * (48000.0 / HostRate);
        Check(Math.Abs(wireFrames - expectedWire) / expectedWire < 0.02,
            $"a 44.1k host must produce ~{expectedWire:0} wire frames, not {wireFrames} — a mismatch here IS the transposition bug");
        var capturedHz = DominantHz(captured, 48000, stride: 2);
        Check(Math.Abs(capturedHz - ToneHz) < 25,
            $"a 1 kHz tone from a 44.1k DAW must still be 1 kHz on the wire (measured {capturedHz:0} Hz)");

        // --- Into the DAW: 48k wire -> host rate ----------------------------------------------------
        // Driven through the REAL bridge, so this measures the whole receive path rather than the
        // resampler in isolation.
        var peer = new IPEndPoint(IPAddress.Parse("192.168.1.50"), 47830);
        var engine = new PlayoutEngine(new ReceiverDiagnostics());
        engine.SetLaneActive(RenderRoute.WasapiLane, true);
        engine.SetMaxLatencyMs(RenderRoute.Mixed, 30);
        var session = engine.GetOrCreateSession(peer, 1, 8 * 1024 * 1024);

        // Feed the tone in AS IT IS CONSUMED, the way a real peer sends it. Dumping four seconds in
        // at once would leave the session massively over-buffered against its 30 ms target, and the
        // drift corrector would do exactly what it is supposed to — race to drain it — which changes
        // the pitch on purpose and would make this measure the wrong thing entirely.
        var tone = new ToneStream(ToneHz, 0.5f);
        tone.WriteInto(session, frames: 48000 / 5);   // 200 ms head start, near the target depth
        session.NoteFramesQueued(30);

        using var host = new PluginBridgeHost(engine.ReadClaimedPeer, port: 0);
        engine.SetPluginPeerClaims(host.Claims);
        using var client = new PluginBridgeClient(host.Port);
        client.ReceiveFrom(peer.Address);

        var render = new PeerRenderBridge(client);
        render.PrepareForBlockSize(HostBlock, HostRate);

        var outLeft = new double[HostBlock];
        var outRight = new double[HostBlock];
        var received = new List<float>();
        var wirePerBlock = (int)Math.Ceiling(HostBlock * 48000.0 / HostRate);
        for (var block = 0; block < 300; block++)
        {
            tone.WriteInto(session, wirePerBlock);     // the peer keeps sending
            var filled = render.FillHostBlock(outLeft, outRight);
            // Skip the first blocks: the ring is still filling, and a partly-empty block is silence
            // followed by audio, which would read as a spurious zero crossing.
            if (block > 20) for (var i = 0; i < filled; i++) received.Add((float)outLeft[i]);
            Thread.Sleep(2);
        }

        Check(received.Count > HostBlock, $"the peer's audio must reach the DAW track at the host's rate ({received.Count} frames)");
        var receivedHz = DominantHz(received, (int)HostRate, stride: 1);
        Check(Math.Abs(receivedHz - ToneHz) < 25,
            $"a 1 kHz peer must still be 1 kHz on a 44.1k DAW track (measured {receivedHz:0} Hz) — this is SonoBus's open bug, avoided");

        return $"44.1k DAW -> 48k wire: {wireFrames} frames, tone held at {capturedHz:0} Hz; "
             + $"48k wire -> 44.1k DAW track through the real bridge: tone held at {receivedHz:0} Hz";
    }


    /// <summary>A continuous tone written into a session in real-time-sized pieces, phase carried
    /// across the writes so the result is one unbroken sine rather than a series of restarts (which
    /// would put a discontinuity at every seam and ruin any pitch measurement).</summary>
    private sealed class ToneStream(double toneHz, float amplitude)
    {
        private long frame;

        public void WriteInto(Receiver.SessionPlayout session, int frames)
        {
            var block = new byte[frames * 2 * sizeof(float)];
            var floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(block.AsSpan());
            var step = 2 * Math.PI * toneHz / 48000;
            for (var i = 0; i < frames; i++)
            {
                var v = (float)(Math.Sin(frame * step) * amplitude);
                floats[i * 2] = v;
                floats[i * 2 + 1] = v;
                frame++;
            }
            session.Write(block);
        }
    }

    /// <summary>Frequency by zero crossings. Crude, and entirely sufficient: the failure being
    /// guarded against shifts the pitch by nearly 9%, not by a hair.</summary>
    private static double DominantHz(IReadOnlyList<float> samples, int sampleRate, int stride)
    {
        var crossings = 0;
        var counted = 0;
        var previous = 0f;
        for (var i = 0; i < samples.Count; i += stride)
        {
            var value = samples[i];
            if (counted > 0 && ((previous < 0 && value >= 0) || (previous >= 0 && value < 0))) crossings++;
            previous = value;
            counted++;
        }
        if (counted < 2) return 0;
        // Two crossings per cycle.
        return crossings / 2.0 * sampleRate / counted;
    }
}
