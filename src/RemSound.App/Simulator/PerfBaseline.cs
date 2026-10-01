using System.Text.Json;
using System.Text.Json.Nodes;

namespace RemSound.App.Simulator;

/// <summary>
/// THE CPU BASELINE (tests\perf-baseline.json, 2026-09-28): what each benchmark cost on a machine, per build (a Debug
/// build's audio code runs unoptimised and costs more), measured by <c>RemSound.exe --simulate perf-baseline</c> as the
/// median of several passes. The gate compares against the entry for the machine and build it is running on, and says so
/// when there is none - a figure from another processor means nothing here.
///
/// <para>The tolerance, from the run-to-run noise measured on 2026-09-28 (i7-13700, 24 logical processors): the three
/// stretches inside one run agree to within 1-3 %; separate runs of the same build to within 5-9 % on the cheapest cases and
/// 1-3 % on the rest, with the odd case up to 17 % off on figures under 0.1 % of a core, and one whole case once 38 % high
/// when something else on the machine took the core's twin. So a case fails only when it is more than
/// <see cref="Tolerance"/> (25 %) plus <see cref="AbsoluteSlackPercent"/> (0.05 % of a core) over its baseline, three
/// times running: noise that large does not repeat, and a real regression does.</para>
/// </summary>
internal static class PerfBaseline
{
    public const double Tolerance = 0.25;
    public const double AbsoluteSlackPercent = 0.05;
    public const int Tries = 3;

    public const string RelativePath = @"tests\perf-baseline.json";

    /// <summary>The source tree, found as the gate finds it: REMSOUND_SOURCE_ROOT, or up from the program's folder.</summary>
    public static string? FindSourceRoot()
    {
        var supplied = Environment.GetEnvironmentVariable("REMSOUND_SOURCE_ROOT");
        if (!string.IsNullOrWhiteSpace(supplied) && Directory.Exists(Path.Combine(supplied, "src", "RemSound.App"))) return supplied;
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "RemSound.App"))) return dir.FullName;
        return null;
    }

    /// <summary>Where the baseline file lives: in the source tree, found as the gate finds it.</summary>
    public static string? PathIn(string? sourceRoot) => sourceRoot is null ? null : Path.Combine(sourceRoot, RelativePath);

    /// <summary>The allowed figure for a case with this baseline.</summary>
    public static double Allowed(double baseline) => baseline * (1 + Tolerance) + AbsoluteSlackPercent;

    /// <summary>This machine and build's figures, or null (no file, or no entry for them).</summary>
    public static IReadOnlyDictionary<string, double>? Load(string? path, string machine, string build, out string said)
    {
        said = "";
        if (path is null || !File.Exists(path)) { said = $"no baseline file at {path ?? RelativePath}"; return null; }
        var root = JsonNode.Parse(File.ReadAllText(path));
        if (root?["entries"] is not JsonArray entries) { said = $"the baseline file {path} has no entries"; return null; }
        foreach (var entry in entries)
        {
            if (entry?["machine"]?.GetValue<string>() != machine || entry["build"]?.GetValue<string>() != build) continue;
            var cases = new Dictionary<string, double>(StringComparer.Ordinal);
            if (entry["cases"] is JsonObject caseList)
                foreach (var (key, value) in caseList)
                    if (value is not null) cases[key] = value.GetValue<double>();
            said = $"the {build} baseline for {machine}, measured {entry["measured"]?.GetValue<string>()} ({cases.Count} cases)";
            return cases;
        }
        said = $"no baseline for {machine}, {build} build, in {path} - measure one with RemSound.exe --simulate perf-baseline";
        return null;
    }

    /// <summary>Write (or replace) this machine and build's entry, keeping every other entry as it was.</summary>
    public static void Save(string path, string machine, string build, IReadOnlyDictionary<string, double> cases, int passes, string note)
    {
        JsonObject root;
        if (File.Exists(path)) root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject();
        else root = new JsonObject();
        root["about"] = "RemSound's audio path: the CPU each benchmark costs, as percent of one core at real time (the simulation thread's own "
            + "CPU per simulated second, the performance rig's own share taken off). One entry per machine and build; the gate step "
            + "PERFORMANCE CPU compares against the entry for the machine and build it runs on. Measured by RemSound.exe --simulate "
            + "perf-baseline (the median of several passes); see src/RemSound.App/Simulator/PerfBaseline.cs for the tolerance and why.";
        root["tolerance"] = $"a case fails at more than {Tolerance:P0} plus {AbsoluteSlackPercent} % of a core over its baseline, {Tries} times running";
        var entries = root["entries"] as JsonArray ?? new JsonArray();
        for (var i = entries.Count - 1; i >= 0; i--)
            if (entries[i]?["machine"]?.GetValue<string>() == machine && entries[i]?["build"]?.GetValue<string>() == build) entries.RemoveAt(i);
        var caseObject = new JsonObject();
        foreach (var (key, value) in cases.OrderBy(kv => kv.Key, StringComparer.Ordinal)) caseObject[key] = Math.Round(value, 4);
        entries.Add(new JsonObject
        {
            ["machine"] = machine,
            ["build"] = build,
            ["measured"] = DateTime.Now.ToString("yyyy-MM-dd"),
            ["passes"] = passes,
            ["note"] = note,
            ["cases"] = caseObject,
        });
        root["entries"] = entries;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Written to be read: apostrophes as apostrophes, not escapes.
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }) + Environment.NewLine);
    }
}
