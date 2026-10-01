namespace RemSound.App.Simulator;

/// <summary>
/// The benchmarks the gate runs and what they are held to (2026-09-28). The absolute budgets say what "very little load"
/// means here, in percent of ONE core at real time, and were set with room over what a Debug build measured on the
/// i7-13700 (a Release build, what ships, measured less; see the report):
/// <list type="bullet">
/// <item>one RemSound in a two-way call with one peer - its sender and its receiver, the whole path - at most
/// <see cref="TwoWayBudget"/> % (measured at most 3.4 % Debug, 2.8 % Release: Opus at 2.5 ms, both kinds of output);</item>
/// <item>the sending end alone at most <see cref="SenderBudget"/> % (measured at most 2.1 %);</item>
/// <item>one receiver playing eight peers at most <see cref="EightPeerReceiverBudget"/> %.</item>
/// </list>
/// </summary>
internal static class PerfSuite
{
    public const double TwoWayBudget = 5;
    public const double SenderBudget = 3;
    public const double EightPeerReceiverBudget = 15;

    public static readonly SimConfig[] Configs = [SimConfig.WasapiOnly, SimConfig.AsioOnly, SimConfig.Both];
    public static readonly SimCodec[] Codecs = [SimCodec.Pcm, SimCodec.Opus2_5, SimCodec.Opus10];

    /// <summary>Every benchmark: each configuration and way of sending with one, four and eight peers into one receiver;
    /// the sender alone; the receiver alone with one peer and with eight; and one peer with the diagnostics switch on (logs or
    /// an auto-tune), whose probes cost CPU of their own.</summary>
    public static List<PerfCase> CpuCases(SimConfig? only = null)
    {
        var cases = new List<PerfCase>();
        foreach (var config in Configs.Where(c => only is null || c == only))
        {
            foreach (var codec in Codecs)
            {
                foreach (var peers in new[] { 1, 4, 8 }) cases.Add(new PerfCase(config, codec, peers));
                cases.Add(new PerfCase(config, codec, 1, PerfPart.SenderAlone));
                cases.Add(new PerfCase(config, codec, 1, PerfPart.ReceiverAlone));
                cases.Add(new PerfCase(config, codec, 8, PerfPart.ReceiverAlone));
            }
            cases.Add(new PerfCase(config, SimCodec.Opus2_5, 1, Diagnostics: true));
        }
        return cases;
    }

    /// <summary>The budget a case is held to, or null if it has none (four and eight peers with their senders all counted
    /// are several machines' work, not one's).</summary>
    public static double? Budget(PerfCase c) => c.Part switch
    {
        PerfPart.Whole when c.Peers == 1 => TwoWayBudget,
        PerfPart.SenderAlone => SenderBudget,
        PerfPart.ReceiverAlone when c.Peers == 8 => EightPeerReceiverBudget,
        _ => null,
    };

    /// <summary>Judge every result: a healthy stream measured, inside its budget, and - with a baseline for this machine and
    /// build - no more than the tolerance over it, three tries running (<paramref name="retry"/> measures a case again).</summary>
    public static PerfVerdict Judge(List<PerfResult> results, IReadOnlyDictionary<string, double>? baseline, string baselineSaid, Func<PerfCase, PerfResult> retry)
    {
        var v = new PerfVerdict();
        var over = new List<string>();
        var within = 0;
        var faster = new List<string>();
        for (var i = 0; i < results.Count; i++)
        {
            var r = results[i];
            var c = r.Case;
            // A benchmark of a stream breaking up measures the wrong thing.
            if (c.Part != PerfPart.SenderAlone)
            {
                if (r.Sessions != c.Streams)
                    v.Fail($"{c.Plain}: premise - the receiver must hold one session per stream ({r.Sessions} for {c.Streams})");
                if (r.Underruns > c.Streams || r.Concealed > c.Streams)
                    v.Fail($"{c.Plain}: premise - the measured stream must play without breaking up ({r.Underruns} underruns, {r.Concealed} concealed)");
            }
            if (r.RigPercent > Math.Max(0.02, r.RawPercent * 0.1))
                v.Fail($"{c.Plain}: the rig's own share must be a small part of what is measured ({r.RigPercent:0.000} of {r.RawPercent:0.000} % of a core)");
            if (Budget(c) is { } budget && r.CorePercent > budget)
                v.Fail($"{c.Plain}: {r.CorePercent:0.00} % of one core is over the budget of {budget:0} % for \"very little load\"");
            if (baseline is null || !baseline.TryGetValue(c.Key, out var b)) continue;
            var best = r;
            for (var attempt = 2; best.CorePercent > PerfBaseline.Allowed(b) && attempt <= PerfBaseline.Tries; attempt++)
            {
                var again = retry(c);
                if (again.CorePercent < best.CorePercent) best = again;
            }
            results[i] = best;
            if (best.CorePercent > PerfBaseline.Allowed(b))
                over.Add($"{c.Plain}: {best.CorePercent:0.000} % of a core against a baseline of {b:0.000} ({(best.CorePercent / b - 1) * 100:+0;-0} %, "
                    + $"allowed {PerfBaseline.Allowed(b):0.000}; best of {PerfBaseline.Tries} tries)");
            else
            {
                within++;
                if (best.CorePercent < b * (1 - PerfBaseline.Tolerance)) faster.Add($"{c.Plain} {best.CorePercent:0.000} against {b:0.000}");
            }
        }
        if (over.Count > 0) v.Fail($"CPU REGRESSION against {baselineSaid}: {string.Join("; ", over)}");
        v.Say(baseline is null ? $"no comparison: {baselineSaid}"
            : $"{within} of {results.Count(r => baseline.ContainsKey(r.Case.Key))} cases within {PerfBaseline.Tolerance:P0} + {PerfBaseline.AbsoluteSlackPercent} of "
              + $"{baselineSaid}{(faster.Count > 0 ? $"; faster than it by more than {PerfBaseline.Tolerance:P0} (a new baseline may be due): {string.Join(", ", faster)}" : "")}");
        v.Summary = Summarise(results);
        return v;
    }

    /// <summary>The figures, in the words the step's line gives them.</summary>
    public static string Summarise(IReadOnlyList<PerfResult> results)
    {
        string Row(Func<PerfCase, bool> which) => string.Join(", ", Configs.Select(config =>
            $"{PerfBench.Name(config)} " + string.Join("/", Codecs.Select(codec =>
                results.FirstOrDefault(r => r.Case.Config == config && r.Case.Codec == codec && which(r.Case)) is { } x ? x.CorePercent.ToString("0.00") : "-"))));
        var diagnostics = results.Where(r => r.Case.Diagnostics).Select(r => $"{PerfBench.Name(r.Case.Config)} {r.CorePercent:0.00}");
        var rig = results.Max(r => r.RawPercent <= 0 ? 0 : r.RigPercent / r.RawPercent);
        return $"percent of one core, PCM/Opus 2.5 ms/Opus 10 ms: a two-way call with one peer {Row(c => c.Part == PerfPart.Whole && c.Peers == 1 && !c.Diagnostics)}; "
             + $"4 peers into one receiver, all their senders counted too, {Row(c => c.Part == PerfPart.Whole && c.Peers == 4)}; 8 peers {Row(c => c.Part == PerfPart.Whole && c.Peers == 8)}; "
             + $"the sender alone {Row(c => c.Part == PerfPart.SenderAlone)}; the receiver alone, one peer {Row(c => c.Part == PerfPart.ReceiverAlone && c.Peers == 1)}, "
             + $"eight peers {Row(c => c.Part == PerfPart.ReceiverAlone && c.Peers == 8)}; one peer with the diagnostics switch on (Opus 2.5 ms) {string.Join(", ", diagnostics)}; "
             + $"the rig's own share at most {rig:P1} of a figure";
    }

    /// <summary>Run every case (after the warm-up), each once.</summary>
    public static List<PerfResult> RunAll(IEnumerable<PerfCase> cases, Action<string>? progress = null)
    {
        PerfBench.WarmUp();
        var results = new List<PerfResult>();
        foreach (var c in cases)
        {
            var r = PerfBench.Run(c);
            results.Add(r);
            progress?.Invoke(PerfBench.Describe(r));
        }
        return results;
    }
}
