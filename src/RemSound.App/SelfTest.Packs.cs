using System.Diagnostics;
using System.Text.RegularExpressions;

namespace RemSound.App;

/// <summary>Whether a step must run on every build, or only when its area changes.</summary>
internal enum StepTier
{
    /// <summary>Runs in both packs: a mechanical guard, something another RemSound or a user's saved state depends on
    /// (the wire, the crypto, the heartbeat, the plugin's identity and state, the server), the basic audio path, start-up.</summary>
    Core,

    /// <summary>Runs in the feature pack only when its area has changed, and always in the full pack.</summary>
    Area,
}

/// <summary>
/// THE TWO PACKS (Ed, 2026-09-27): "divide the test pack into 2. a pack we can run for every feature you put in, focusing on
/// the new features, the very high priority stuff, and then the full regression suite which we run before every release."
///
/// <para><b>Full</b> (<c>--pack full</c>, the default): every step. Before every release, and whenever Ed asks.</para>
///
/// <para><b>Feature</b> (<c>--pack feature</c>): what a feature build must never skip, and what the change touches -
/// every <see cref="StepTier.Core"/> step; every step of an area the change touches (worked out from the files changed
/// since the last commit, plus any named with <c>--areas</c>); every step whose test file changed; and every step added
/// since the last release, which is to say the features not yet released. Its result says FEATURE PACK and names what it
/// left for the full run, so it can never be taken for a release gate.</para>
///
/// <para>Every step says its tier and area in <see cref="PackCatalog"/>. A step missing from it, or an entry for a step
/// that no longer exists, fails the guard at the end of the run, so a new step can never fall into neither pack.</para>
/// </summary>
internal static partial class SelfTest
{
    /// <summary>The areas a step can belong to. Kept short and fixed, so a changed file maps onto them.</summary>
    internal static readonly string[] PackAreas =
    [
        "send", "receive", "buffer-tuner", "codec", "crypto-wire", "peers-heartbeat", "relay-server", "plugin", "service",
        "recording", "dialogs-ui", "accessibility", "context-help-manual", "settings-profiles", "hotkeys", "cues-sounds",
        "updates", "latency-lab", "simulator", "headless-cli", "logging", "guards", "resources-perf", "push-to-talk", "metronome", "other",
    ];

    private static bool featurePack;
    private static readonly HashSet<string> packAreas = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> packChangedTestFiles = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> packNewSinceRelease = new(StringComparer.Ordinal);
    private static readonly List<string> registeredSteps = new();
    private static readonly List<string> leftForFullRun = new();
    private static string packWhy = "";

    /// <summary>The feature pack is running: steps that do the same thing many times (the F1 sweep) check a sample.</summary>
    internal static bool FeaturePack => featurePack;

    /// <summary>Read <c>--pack</c> and <c>--areas</c>, and in the feature pack work out what changed.</summary>
    private static void ConfigurePack(string[] args)
    {
        featurePack = string.Equals(ValueAfter(args, "--pack"), "feature", StringComparison.OrdinalIgnoreCase);
        packAreas.Clear();
        packChangedTestFiles.Clear();
        packNewSinceRelease.Clear();
        registeredSteps.Clear();
        leftForFullRun.Clear();
        packWhy = "";
        if (!featurePack) return;

        var named = (ValueAfter(args, "--areas") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var area in named.Where(a => !a.Equals("auto", StringComparison.OrdinalIgnoreCase))) packAreas.Add(area);

        var root = FindSourceRoot();
        var why = new List<string>();
        if (root is null)
        {
            // Nothing to work out what changed from: run every area rather than guess.
            foreach (var area in PackAreas) packAreas.Add(area);
            packWhy = "the source tree is not reachable, so every area runs";
            return;
        }
        var changed = Git(root, "diff --name-only HEAD") is { } tracked ? tracked.Concat(Git(root, "ls-files --others --exclude-standard") ?? []).ToList() : null;
        if (changed is null)
        {
            foreach (var area in PackAreas) packAreas.Add(area);
            packWhy = "git could not say what changed, so every area runs";
            return;
        }
        foreach (var path in changed)
        {
            var file = Path.GetFileName(path);
            if (file.StartsWith("SelfTest", StringComparison.OrdinalIgnoreCase) && file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                packChangedTestFiles.Add(file);
            else
                foreach (var area in AreasForPath(path)) packAreas.Add(area);
        }
        why.Add($"{changed.Count} changed file(s)");

        // The features not yet released: every step whose name the last release's self-test did not have.
        var tag = Git(root, "describe --tags --abbrev=0 --match v[0-9]*")?.FirstOrDefault();
        var released = tag is null ? null : Git(root, $"show {tag}:src/RemSound.App/SelfTest.cs");
        if (released is not null)
        {
            var then = new HashSet<string>(StepNamesIn(string.Join("\n", released)), StringComparer.Ordinal);
            var now = File.Exists(Path.Combine(root, "src", "RemSound.App", "SelfTest.cs"))
                ? StepNamesIn(File.ReadAllText(Path.Combine(root, "src", "RemSound.App", "SelfTest.cs")))
                : [];
            foreach (var name in now.Where(n => !then.Contains(n))) packNewSinceRelease.Add(name);
            why.Add($"{packNewSinceRelease.Count} step(s) new since {tag}");
        }
        packWhy = string.Join(", ", why);
    }

    /// <summary>The step names registered in a copy of SelfTest.cs.</summary>
    internal static IEnumerable<string> StepNamesIn(string source) =>
        Regex.Matches(source, @"RunStep\(results,\s*""((?:[^""\\]|\\.)*)""").Select(m => Regex.Unescape(m.Groups[1].Value));

    /// <summary>Is this step in the pack being run? Records every name it is asked about, for the guard.</summary>
    private static bool InPack(string name)
    {
        registeredSteps.Add(name);
        if (!featurePack) return true;
        if (!PackCatalog.TryGetValue(name, out var entry)) return true;   // not catalogued: run it, and the guard fails it
        var run = entry.Tier == StepTier.Core || packAreas.Contains(entry.Area) || packChangedTestFiles.Contains(entry.File)
                  || packNewSinceRelease.Contains(name);
        if (!run) leftForFullRun.Add(name);
        return run;
    }

    /// <summary>The areas a changed file belongs to. Deliberately generous: a file that could be any of several runs them all.</summary>
    internal static IEnumerable<string> AreasForPath(string path)
    {
        var p = path.Replace('\\', '/');
        var name = Path.GetFileNameWithoutExtension(p);
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool Has(params string[] words) => words.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase));

        if (p.StartsWith("server/", StringComparison.OrdinalIgnoreCase)) found.Add("relay-server");
        if (p.StartsWith("pi-sender/", StringComparison.OrdinalIgnoreCase)) { found.Add("crypto-wire"); found.Add("receive"); }
        if (p.StartsWith("src/RemSound.Plugin/", StringComparison.OrdinalIgnoreCase) || p.Contains("AudioPlugSharp", StringComparison.OrdinalIgnoreCase)) found.Add("plugin");
        // Push to talk's gate sits on the send lanes, so a change to any of them runs its steps too (2026-09-28).
        if (p.StartsWith("src/RemSound.Sender/", StringComparison.OrdinalIgnoreCase)) { found.Add("send"); found.Add("codec"); found.Add("push-to-talk"); }
        if (p.StartsWith("src/RemSound.Receiver/", StringComparison.OrdinalIgnoreCase)) { found.Add("receive"); found.Add("buffer-tuner"); found.Add("codec"); }
        if (p.StartsWith("src/RemSound.Ui/", StringComparison.OrdinalIgnoreCase)) { found.Add("accessibility"); found.Add("dialogs-ui"); }
        if (p.StartsWith("default sounds/", StringComparison.OrdinalIgnoreCase)) found.Add("cues-sounds");
        if (p.StartsWith("third-party/remsound-opus/", StringComparison.OrdinalIgnoreCase)) { found.Add("codec"); found.Add("latency-lab"); }
        if (p.StartsWith("Install and Uninstall Scripts/", StringComparison.OrdinalIgnoreCase)) { found.Add("headless-cli"); found.Add("service"); found.Add("plugin"); }
        if (p.Equals("readme.html", StringComparison.OrdinalIgnoreCase) || p.Equals("MANUAL.md", StringComparison.OrdinalIgnoreCase)
            || Has("ContextHelp", "HelpLauncher", "HelpSound", "HelpKey")) found.Add("context-help-manual");
        if (p.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase) || p.StartsWith("tools/", StringComparison.OrdinalIgnoreCase)) found.Add("guards");

        if (Has("PushToTalk")) { found.Add("push-to-talk"); found.Add("send"); found.Add("recording"); found.Add("hotkeys"); }
        // The metronome's clicks are mixed on every output lane, recorded, and offered to the DAW (2026-09-28).
        if (Has("Metronome")) { found.Add("metronome"); found.Add("receive"); found.Add("recording"); found.Add("plugin"); found.Add("hotkeys"); }
        if (p.StartsWith("src/RemSound.Receiver/", StringComparison.OrdinalIgnoreCase) || p.StartsWith("default sounds/metronome/", StringComparison.OrdinalIgnoreCase)) found.Add("metronome");
        if (Has("Relay", "Server")) found.Add("relay-server");
        if (Has("Plugin")) found.Add("plugin");
        if (Has("Service")) found.Add("service");
        if (Has("Record")) found.Add("recording");
        if (Has("Profile", "AppConfig", "Settings")) found.Add("settings-profiles");
        if (Has("Hotkey")) found.Add("hotkeys");
        if (Has("Cue", "KeyClick", "Sound")) found.Add("cues-sounds");
        if (Has("Update", "Installer")) found.Add("updates");
        if (Has("LatencyLab", "JamulusStyle", "DropoutTuner", "AsioBufferSizing", "OpusCustom", "StreamBuffers", "LowestDelay")) found.Add("latency-lab");
        if (Has("Simulat", "SimLab", "SimRunner", "SimAnalysis", "AudioClock")
            || p.Contains("/Simulator/", StringComparison.OrdinalIgnoreCase)) found.Add("simulator"); // the whole folder (SimMainWindow, 2026-09-28)
        if (Has("RemoteControl", "Windowless", "CommandLine", "Headless", "Program")) found.Add("headless-cli");
        if (Has("Log")) found.Add("logging");
        if (Has("Heartbeat", "PeerDiscovery", "Peer", "Accept", "TickProof")) found.Add("peers-heartbeat");
        if (Has("Crypto", "RemPacket", "AudioFormatInfo", "Wire", "Password")) found.Add("crypto-wire");
        if (Has("Dialog", "Preferences", "Form", "Tray", "Menu")) { found.Add("dialogs-ui"); found.Add("accessibility"); found.Add("context-help-manual"); }
        if (Has("Accessib", "ScreenReader", "Tolk")) found.Add("accessibility");
        if (Has("Opus", "Codec", "Pcm")) found.Add("codec");
        if (Has("Asio", "Wasapi", "Audio", "Capture", "Render", "Playout", "Mixing", "Device", "Stream", "Session")) { found.Add("send"); found.Add("receive"); found.Add("buffer-tuner"); }
        if (Has("Tune", "Latency", "Buffer")) found.Add("buffer-tuner");
        if (Has("Performance", "Resource", "Perf", "Soak", "ThreadCpu")) found.Add("resources-perf"); // the performance rig and its suite (2026-09-28)
        // The main window is every area's front end.
        if (name.StartsWith("MainForm", StringComparison.OrdinalIgnoreCase)) { found.Add("dialogs-ui"); found.Add("settings-profiles"); found.Add("accessibility"); }
        if (name.Equals("MainForm", StringComparison.OrdinalIgnoreCase)) { found.Add("send"); found.Add("receive"); found.Add("peers-heartbeat"); found.Add("buffer-tuner"); }

        // Notes, plans and logs change nothing the steps check.
        var notes = p.EndsWith(".md", StringComparison.OrdinalIgnoreCase) && !p.Equals("MANUAL.md", StringComparison.OrdinalIgnoreCase)
                    || p.StartsWith("simulation-results/", StringComparison.OrdinalIgnoreCase);
        if (found.Count == 0 && !notes) found.Add("other");
        return found;
    }

    /// <summary>Gate seam: what to start in git's place (the test floods its error stream).</summary>
    internal static Func<string, string, ProcessStartInfo>? GitStartForTest;

    /// <summary>git's answer, one line per item, or null when git is not there, said no, or took over 30 s.
    /// Both of its streams are read at once. They were read one after the other, and a git that wrote more warnings than a
    /// pipe holds ("LF will be replaced by CRLF", one per file) waited for its error stream to be read while this waited for
    /// its output to end: the feature pack hung for six hours before running a step (2026-09-29).</summary>
    internal static List<string>? Git(string root, string arguments)
    {
        try
        {
            var start = GitStartForTest?.Invoke(root, arguments) ?? new ProcessStartInfo("git", $"-C \"{root}\" {arguments}");
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            using var p = Process.Start(start);
            if (p is null) return null;
            var output = p.StandardOutput.ReadToEndAsync();
            var errors = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(30000))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* gone already */ }
                return null;
            }
            if (!Task.WaitAll([output, errors], 5000) || p.ExitCode != 0) return null;
            return output.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>GATE GUARD: every step says which pack it belongs in, and the catalog names no step that has gone. A step
    /// missing from the catalog would run in the feature pack without anyone having decided it should; a stale entry is a
    /// decision about nothing.</summary>
    private static string EveryStepIsInAPack()
    {
        var registered = new HashSet<string>(registeredSteps, StringComparer.Ordinal);
        Check(registered.Count > 300, $"the run must have seen every step's name ({registered.Count})");
        var missing = registered.Where(n => !PackCatalog.ContainsKey(n)).ToList();
        Check(missing.Count == 0, $"{missing.Count} step(s) say nothing about which pack they belong in - add each to PackCatalog "
            + $"(SelfTest.PackCatalog.cs) as Core or with its area: {string.Join(" | ", missing.Take(8))}");
        var stale = PackCatalog.Keys.Where(n => !registered.Contains(n)).ToList();
        Check(stale.Count == 0, $"{stale.Count} catalog entr(ies) name a step that no longer exists: {string.Join(" | ", stale.Take(8))}");
        var badAreas = PackCatalog.Where(kv => !PackAreas.Contains(kv.Value.Area)).Select(kv => $"{kv.Key} ({kv.Value.Area})").ToList();
        Check(badAreas.Count == 0, $"catalog entries with an area that is not one of the fixed list: {string.Join(" | ", badAreas.Take(8))}");
        var core = PackCatalog.Count(kv => kv.Value.Tier == StepTier.Core);
        Check(core >= 20 && core < PackCatalog.Count / 2, $"the core must be the few steps every build runs, not none and not most ({core} of {PackCatalog.Count})");
        foreach (var (path, want) in new[]
                 {
                     // Files only the folder rule can place - a name that also matches a word would hide a broken folder
                     // rule (it did, 2026-09-27: SessionPlayout.cs matched "Session").
                     ("src/RemSound.Receiver/NetworkListener.cs", "receive"), ("src/RemSound.Sender/SenderLane.cs", "send"),
                     ("server/remsound-relay.py", "relay-server"), ("readme.html", "context-help-manual"),
                     ("src/RemSound.App/PreferencesDialog.cs", "dialogs-ui"), ("src/RemSound.Core/DropoutTuner.cs", "latency-lab"),
                     ("src/RemSound.Plugin/RemSoundPlugin.cs", "plugin"), ("src/RemSound.App/NewThing.cs", "other"),
                     ("src/RemSound.Core/PushToTalk.cs", "push-to-talk"), ("src/RemSound.Sender/SenderLane.cs", "push-to-talk"),
                 })
            Check(AreasForPath(path).Contains(want), $"a change to {path} must run the {want} steps (it maps to {string.Join(", ", AreasForPath(path))})");
        Check(!AreasForPath("PLAN-something.md").Any(), "a note or plan changes nothing a step checks");
        return $"{registered.Count} steps, each in the catalog: {core} core, {PackCatalog.Count - core} by area; changed files map onto areas";
    }
}
