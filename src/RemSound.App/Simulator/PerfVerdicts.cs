namespace RemSound.App.Simulator;

/// <summary>What a performance run concluded: the lines to report, and the failures (empty when it passed). The gate's
/// steps turn each failure into a failed check; <c>--simulate</c> prints them.</summary>
internal sealed class PerfVerdict
{
    public List<string> Lines { get; } = new();
    /// <summary>Every failure, with the configuration it belongs to (none: the process as a whole).</summary>
    public List<(SimConfig? Config, string Why)> Failed { get; } = new();
    public List<string> Failures => Failed.Select(f => f.Why).ToList();
    public string Summary { get; set; } = "";

    public void Fail(string why) => Failed.Add((null, why));
    public void FailFor(SimConfig config, string why) => Failed.Add((config, why));
    public void Say(string line) => Lines.Add(line);
    public List<string> FailuresFor(SimConfig? config) => Failed.Where(f => f.Config == config).Select(f => f.Why).ToList();
}

/// <summary>The bars the soak, the stress runs and the benchmarks are judged by, each with the measurement it was set from
/// (2026-09-28). See the report in simulation-results for the runs behind them.</summary>
internal static class PerfVerdicts
{
    // ---- the soak ----

    /// <summary>The managed heap, after a full collection, may grow by no more than this over the idle hours of the day,
    /// judged by the straight line through every idle sample. Measured 2026-09-28: a trend of +10 to +17 KB a day, 15 to 22 KB
    /// from lowest to highest over 43 samples - the same objects every half hour. One four-byte entry kept per packet during
    /// the call would be 6 MB over its two hours, and one session kept per visit 2 MB each.</summary>
    public const double HeapGrowthBytesPerDay = 1024 * 1024;

    /// <summary>How far handles and threads may climb across the day's idle samples - from the lowest of the first three to
    /// the highest of the last three, or along the trend. Measured 2026-09-28: threads anywhere from 16 to 23 and handles
    /// within 8 of each other, as the runtime's pool and Windows' own workers come and go (in both directions). One thread or
    /// handle kept per visit would be 42 over the day.</summary>
    public const int HandleWander = 32;
    public const int ThreadWander = 6;

    /// <summary>A call's CPU per simulated second in its last half hour against its second half hour (the first holds the
    /// start-up): a cost that creeps with time is a list scanned per packet that grows.</summary>
    public const double CpuCreepRatio = 1.3;

    public static PerfVerdict Soak(SoakResult r)
    {
        var v = new PerfVerdict();
        var o = r.Options;
        foreach (var (config, why) in r.Failures) v.FailFor(config, $"{PerfBench.Name(config)}: the soak stopped - {why}");
        var call = r.Samples.Where(s => s.Call && s.Hours <= o.CallHours + 1e-6).ToList();
        // The steady samples the trends are fitted to: the idle hours; or, for one call all day long, the call after its first hour.
        var quiet = r.Samples.Where(s => !s.Call).ToList();
        var allCall = quiet.Count < 3;
        var idle = allCall ? call.Where(s => s.Hours > 1).ToList() : quiet;
        var steadily = allCall ? "through the call" : "through the idle hours";
        v.Say($"{o.Hours:0.#} simulated hours, each of {string.Join(", ", o.ConfigsOrAll.Select(PerfBench.Name))} side by side, in {r.WallSeconds:0} s: "
            + $"a {o.CallHours:0.#}-hour call, a visitor for {o.VisitSeconds:0} s every {o.VisitEveryMinutes:0} minutes, and a {o.EveningCallMinutes:0}-minute call at the end");

        // ---- the process ----
        if (idle.Count >= 3)
        {
            var heap = idle.Select(s => (s.Hours, (double)s.Process.HeapBytes)).ToList();
            var slope = SimSoak.Slope(heap);
            var perDay = slope * 24;
            var span = idle.Max(s => s.Process.HeapBytes) - idle.Min(s => s.Process.HeapBytes);
            v.Say($"heap after a full collection, {steadily} ({idle.Count} samples, {idle[0].Hours:0.#} to {idle[^1].Hours:0.#} h): "
                + $"{idle[0].Process.HeapMb:0.000} MB to {idle[^1].Process.HeapMb:0.000} MB, trend {perDay / 1024:+0.0;-0.0} KB a day, "
                + $"{span / 1024.0:0.0} KB from lowest to highest");
            if (perDay > HeapGrowthBytesPerDay)
                v.Fail($"the managed heap grows {steadily}: {perDay / 1048576:0.00} MB a day along its trend (bar {HeapGrowthBytesPerDay / 1048576:0} MB) - "
                    + $"{idle[0].Process.HeapMb:0.00} MB at {idle[0].Hours:0.#} h, {idle[^1].Process.HeapMb:0.00} MB at {idle[^1].Hours:0.#} h");
            // Growth only: from the lowest of the first three samples to the highest of the last three (the process's pool
            // threads come and go, and going is not a leak).
            var handles = idle.TakeLast(3).Max(s => s.Process.Handles) - idle.Take(3).Min(s => s.Process.Handles);
            var threads = idle.TakeLast(3).Max(s => s.Process.Threads) - idle.Take(3).Min(s => s.Process.Threads);
            var handleSlope = SimSoak.Slope(idle.Select(s => (s.Hours, (double)s.Process.Handles)).ToList()) * 24;
            var threadSlope = SimSoak.Slope(idle.Select(s => (s.Hours, (double)s.Process.Threads)).ToList()) * 24;
            v.Say($"handles {idle.Min(s => s.Process.Handles)} to {idle.Max(s => s.Process.Handles)} (trend {handleSlope:+0.0;-0.0} a day), "
                + $"threads {idle.Min(s => s.Process.Threads)} to {idle.Max(s => s.Process.Threads)} (trend {threadSlope:+0.0;-0.0} a day)");
            if (handles > HandleWander || handleSlope > HandleWander)
                v.Fail($"the process's handles climb {steadily}: {idle[0].Process.Handles} at {idle[0].Hours:0.#} h, {idle[^1].Process.Handles} at the end, "
                    + $"{handleSlope:+0;-0} a day along the trend (bar {HandleWander})");
            if (threads > ThreadWander || threadSlope > ThreadWander)
                v.Fail($"the process's threads climb {steadily}: {string.Join(" ", idle.Select(s => s.Process.Threads))} (bar {ThreadWander})");
        }
        else v.Fail($"premise: the soak must look at the process {steadily} at least three times ({idle.Count})");
        if (call.Count >= 2)
        {
            var growth = call[^1].Process.HeapBytes - call[0].Process.HeapBytes;
            v.Say($"heap during the call: {call[0].Process.HeapMb:0.000} MB at {call[0].Hours:0.#} h, {call[^1].Process.HeapMb:0.000} MB at {call[^1].Hours:0.#} h");
            if (growth > HeapGrowthBytesPerDay * o.CallHours / 24 + 256 * 1024)
                v.Fail($"the managed heap grows during the call: {growth / 1048576.0:+0.00} MB from {call[0].Hours:0.#} to {call[^1].Hours:0.#} h");
        }

        // ---- each configuration: its tables, its call, its CPU, what it sounded like ----
        foreach (var config in o.ConfigsOrAll)
        {
            var name = PerfBench.Name(config);
            // Every table the same size in every idle sample: nothing kept per stream or per peer piles up.
            var names = idle.SelectMany(s => s.Tables.GetValueOrDefault(config) ?? []).Select(t => t.Table).Distinct().ToList();
            var grew = new List<string>();
            foreach (var table in names)
            {
                var counts = idle.Select(s => (s.Tables.GetValueOrDefault(config) ?? []).Where(t => t.Table == table).Sum(t => t.Count)).ToList();
                if (counts.Max() != counts.Min()) grew.Add($"{table}: {string.Join(" ", counts)}");
            }
            if (grew.Count > 0) v.FailFor(config, $"{name}: tables change size {steadily}, sample to sample: {string.Join("; ", grew)}");
            var sessionsIdle = idle.Select(s => (s.Tables.GetValueOrDefault(config) ?? []).Where(t => t.Table == "receiver: stream sessions").Sum(t => t.Count)).DefaultIfEmpty(0).Max();
            if (!allCall && sessionsIdle != 0) v.FailFor(config, $"{name}: with nobody sending, the receiver still holds {sessionsIdle} stream session(s) - pruning left them");

            if (r.CallCounters.TryGetValue(config, out var c))
            {
                var streams = config == SimConfig.Both ? 2 : 1;
                var problems = new List<string>();
                if (c.Underruns > 2 * streams) problems.Add($"{c.Underruns} underruns");
                if (c.Concealed > 2 * streams) problems.Add($"{c.Concealed} concealed gaps");
                if (c.LateDropped > 0) problems.Add($"{c.LateDropped} packets taken for late and dropped");
                if (c.Restarts > 0) problems.Add($"{c.Restarts} sequence restarts");
                if (c.Fec + c.Unrecovered + c.PcmConcealed > 0) problems.Add($"{c.Fec + c.Unrecovered + c.PcmConcealed} repairs for packets never lost");
                if (c.Trims > 0) problems.Add($"{c.Trims} click-trims");
                if (c.Drops > 0) problems.Add($"{c.Drops} bytes dropped");
                if (c.Lost > 0) problems.Add($"{c.Lost} packets lost by a network that loses none");
                if (problems.Count > 0) v.FailFor(config, $"{name}: the {o.CallHours:0.#}-hour call was not clean: {string.Join(", ", problems)}");
                if (o.WrapWireCountersAfterPackets > 0 && c.PacketsSent <= o.WrapWireCountersAfterPackets)
                    v.FailFor(config, $"{name}: premise - the call must send past the wire counters' wrap at 2^32 ({c.PacketsSent} packets sent, the wrap after {o.WrapWireCountersAfterPackets})");
                if (c.RingBytesWritten <= int.MaxValue)
                    v.FailFor(config, $"{name}: premise - the call must write past the ring positions' wrap at 2^31 bytes ({c.RingBytesWritten} bytes)");
                v.Say($"{name}: the call sent {c.PacketsSent} packets (its wire counters wrapping past 2^32 after {o.WrapWireCountersAfterPackets}) and "
                    + $"wrote {c.RingBytesWritten / 1e9:0.00} GB into the rings (2^31 is 2.15): {c.Underruns} underruns, {c.Concealed} concealed, "
                    + $"{c.LateDropped} late-dropped, {c.Restarts} restarts, {c.Fec + c.Unrecovered + c.PcmConcealed} repairs, {c.Trims} trims, {c.Drops} bytes dropped");
            }
            else v.FailFor(config, $"{name}: premise - the call's own figures must have been taken as it ended");

            var callCpu = call.Select(s => s.CpuPercent.GetValueOrDefault(config)).ToList();
            if (callCpu.Count >= 3)
            {
                var ratio = callCpu[^1] / Math.Max(1e-9, callCpu[1]);
                v.Say($"{name}: the call's CPU each half hour {string.Join(", ", callCpu.Select(x => $"{x:0.000}"))} % of a core; "
                    + $"idle {idle.Select(s => s.CpuPercent.GetValueOrDefault(config)).DefaultIfEmpty(0).Average():0.000} %");
                if (ratio > CpuCreepRatio)
                    v.FailFor(config, $"{name}: the call's cost creeps: {callCpu[1]:0.000} % of a core in its second half hour, {callCpu[^1]:0.000} % in its last "
                        + $"({ratio:0.00} times, bar {CpuCreepRatio})");
            }

            if (r.Samples.Count > 0 && r.Samples[^1].Counters.TryGetValue(config, out var day))
            {
                v.Say($"{name}: the day's totals {day.PacketsReceived} packets and {day.BytesReceived / 1e9:0.00} GB received of {day.PacketsSent} and "
                    + $"{day.BytesSent / 1e9:0.00} GB sent, {day.SessionsOpened} sessions opened");
                if (day.PacketsReceived != day.PacketsSent - day.Lost || day.BytesReceived <= 0 || day.PacketsReceived <= 0)
                    v.FailFor(config, $"{name}: the day's counters disagree - {day.PacketsReceived} packets received of {day.PacketsSent} sent and {day.Lost} lost");
            }

            if (r.Heard.TryGetValue(config, out var f))
            {
                v.Say($"{name}: the evening call's last {o.ListenSeconds:0} s: {(f.Clean ? "clean" : $"{f.Glitches} break-ups")}, {f.ClicksHeard} clicks heard, "
                    + $"delay {f.DelayMedianMs:0.0} ms, in tune to {f.WorstPitchCents:0.0} cents");
                if (!f.Clean || f.ClicksHeard < o.ListenSeconds * 2 - 3)
                    v.FailFor(config, $"{name}: after a day the evening call must play clean ({f.ClicksHeard} clicks heard, {f.Glitches} break-ups, "
                        + $"{f.StepClicks} clicks, first at {f.FirstGlitchAt})");
            }
            else v.FailFor(config, $"{name}: premise - the evening call must be listened to");
        }
        var afterGrowth = r.After.HeapBytes - r.Before.HeapBytes;
        v.Say($"the process before the soak: {r.Before}; after it, every rig closed: {r.After}");
        v.Summary = v.Failures.Count == 0 ? "held" : $"{v.Failures.Count} problem(s)";
        _ = afterGrowth;
        return v;
    }
}
