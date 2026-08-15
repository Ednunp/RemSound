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

        (bool Sending, string? Peer)? lastJob = null;
        panel.JobChanged += (sending, peer) => lastJob = (sending, peer);

        // --- The peer list comes from the app, with NAMES -----------------------------------------
        panel.Refresh(fromTimer: false);
        Check(panel.PeerItemCountForTest == 2, $"the peer list must be filled from the app ({panel.PeerItemCountForTest} items)");
        Check(panel.PeerLabelsForTest.SequenceEqual(new[] { "Andre", "Chris" }),
            "peers must be shown by NAME — a list of IP addresses is no use to somebody choosing who to put on a track");

        // THE SCREEN-READER RULE: refreshing must not rebuild an unchanged list. A rebuild
        // re-announces every item and moves the user's place — the control would be unusable, and
        // nothing about it would look wrong to a sighted tester.
        panel.SelectPeerForTest(1);
        panel.Refresh(fromTimer: true);
        panel.Refresh(fromTimer: true);
        Check(panel.SelectedPeerIndexForTest == 1,
            "a refresh with an unchanged list must leave the user exactly where they were");

        // ...but a real change must land, and must keep the user on the same PERSON if they are
        // still there, rather than on the same row number.
        peers.Insert(0, ("192.168.1.49", "Jonathan"));
        panel.Refresh(fromTimer: true);
        Check(panel.PeerItemCountForTest == 3, "a peer appearing in RemSound must appear here without reopening the plugin");
        Check(panel.ChosenPeerAddress == "192.168.1.51",
            $"the user must stay on the same PERSON when the list changes, not the same row (landed on {panel.ChosenPeerAddress})");

        // --- One person per track: the job the user picked must reach the engine -------------------
        panel.SelectJobForTest(1);   // receive
        Check(lastJob is { Sending: false }, "choosing 'receive' must tell the engine to receive");
        Check(lastJob?.Peer == "192.168.1.51", $"...and which peer (got {lastJob?.Peer})");
        Check(panel.PeerListEnabledForTest, "the peer chooser must be usable when receiving");

        panel.SelectJobForTest(0);   // send
        Check(lastJob is { Sending: true, Peer: null },
            "choosing 'send' must release the peer — otherwise they stay mute in RemSound with the plugin no longer playing them");
        Check(!panel.PeerListEnabledForTest,
            "the peer chooser must be disabled, not hidden, when sending — hiding it would shift the tab order under a screen-reader user mid-session");

        // --- Active is the user's own bypass ------------------------------------------------------
        panel.SelectJobForTest(1);
        Check(lastJob is { Sending: false }, "back to receiving");
        panel.SetActiveForTest(false);
        Check(lastJob is { Sending: true, Peer: null },
            "unticking Active must hand the peer back to RemSound's speakers, not leave them playing nowhere");
        panel.SetActiveForTest(true);
        Check(lastJob?.Peer == "192.168.1.51", "re-ticking Active must take the peer back");

        // --- The status line must say what is true, including the awkward cases -------------------
        status = "Receiving Andre onto this track.";
        panel.Refresh(fromTimer: true);
        Check(panel.StatusTextForTest.Contains("Receiving Andre"), "the status line must show what the engine reports");

        return "peer list arrives from the app with names; an unchanged refresh never moves the user; a changed list keeps them on the same person; "
             + "job and peer reach the engine; Active works as a bypass that returns the peer";
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
            var initialize = pluginType!.GetMethod("Initialize");
            var hostProperty = pluginType.GetProperty("Host");
            Check(hostProperty is not null, "the plugin must expose Host, or the DAW cannot hand it one");
            hostProperty!.SetValue(instance, new StubAudioHost());
            try { initialize!.Invoke(instance, null); }
            catch (Exception ex)
            {
                var inner = ex.InnerException ?? ex;
                Check(false, $"the plugin must INITIALISE from the shipped folder, not just construct: {inner.GetType().Name}: {inner.Message}");
            }

            // Tidy up: close its link and log rather than leaving a socket open in the gate.
            try { pluginType.GetMethod("CloseForTest", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.Invoke(instance, null); }
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
        Check(parameters.Count >= 3,
            $"the three decisions the window offers must ALSO be parameters, or a host with broken window focus leaves no way in ({parameters.Count} found)");

        foreach (var id in new[] { "job", "peer", "active" })
        {
            var parameter = parameters.FirstOrDefault(p => p.ID == id);
            Check(parameter is not null, $"parameter '{id}' must exist");
            Check(!string.IsNullOrWhiteSpace(parameter!.Name), $"parameter '{id}' must have a name");
            // Read aloud, out of context, with no screen to look at. "Mode" tells somebody nothing.
            Check(parameter.Name.Length > 4 && parameter.Name.Any(char.IsLower),
                $"parameter '{id}' is named '{parameter.Name}' - it must read as plain English when spoken alone");
        }

        var job = parameters.First(p => p.ID == "job");
        var peer = parameters.First(p => p.ID == "peer");
        var active = parameters.First(p => p.ID == "active");

        Check(Math.Abs(job.DefaultValue) < 0.001, "a fresh instance must default to SENDING - it cannot know whose audio you wanted");
        Check(Math.Abs(active.DefaultValue - 1) < 0.001, "and to being active, or the plugin would appear to do nothing when first added");
        Check(Math.Abs(peer.DefaultValue) < 0.001, "with no peer chosen");

        // Moving a parameter must reach the engine. With no app running there are no known peers, so
        // the peer choice can only resolve to nobody - which is the point of the next check.
        job.EditValue = 1;
        plugin.ApplyParameters();
        Check(!plugin.IsSending, "setting 'receive' must switch the instance to receiving");

        peer.EditValue = 5;                 // a peer that isn't there
        plugin.ApplyParameters();
        Check(plugin.ChosenPeerForTest is null,
            "a peer index past the end of the list must resolve to NOBODY - wrapping round would put a stranger on the track when somebody disconnects");

        active.EditValue = 0;
        plugin.ApplyParameters();
        Check(plugin.IsSending, "unticking Active must release the peer, exactly as the window's Active box does");

        plugin.Stop();   // releases the peer and closes the link, as a host deactivating the instance does
        plugin.CloseForTest();   // ...and drop its log, so it is not still ticking during the logging test
        return $"{parameters.Count} named parameters, defaulting to send/active/nobody; job and Active reach the engine; "
             + "an out-of-range peer resolves to nobody rather than wrapping onto somebody else";
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
