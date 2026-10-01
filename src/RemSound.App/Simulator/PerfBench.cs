using System.Diagnostics;

namespace RemSound.App.Simulator;

/// <summary>One benchmark: a configuration, a way of sending, how many peers send into the one receiver, which half of
/// the path is measured, and whether the app's diagnostics switch (logs or an auto-tune) is on.</summary>
internal sealed record PerfCase(SimConfig Config, SimCodec Codec, int Peers, PerfPart Part = PerfPart.Whole, bool Diagnostics = false)
{
    /// <summary>The key the baseline file keeps it under.</summary>
    public string Key => $"{Part} {Config} {Codec} x{Peers}{(Diagnostics ? " diagnostics" : "")}";

    /// <summary>Streams the receiver takes: one per peer, two in "both" (the ASIO lane and the WASAPI lane).</summary>
    public int Streams => Part == PerfPart.SenderAlone ? 0 : Peers * (Config == SimConfig.Both ? 2 : 1);

    public string Plain => $"{PerfBench.Name(Config)}, {SimLab.Name(Codec)}, "
        + (Part switch
        {
            PerfPart.SenderAlone => "the sender alone",
            PerfPart.ReceiverAlone => Peers == 1 ? "the receiver alone, one peer" : $"the receiver alone, {Peers} peers",
            _ => Peers == 1 ? "one peer" : $"{Peers} peers into one receiver",
        })
        + (Diagnostics ? ", diagnostics on" : "");
}

/// <summary>What a benchmark measured. CPU is percent of one core at real time: the simulation thread's own CPU time per
/// simulated second, times 100 - with the rig's own share (measured by the same run with every call into RemSound left
/// out) taken off. Allocations are bytes a simulated second, from the calls into RemSound only.</summary>
internal sealed record PerfResult(PerfCase Case, double CorePercent, double RawPercent, double RigPercent, double[] Windows,
    double SendAllocPerSec, double ReceiveAllocPerSec, double RenderAllocPerSec, double EverySecondAllocPerSec, double WallSeconds,
    double TimesCrossCheckPercent)
{
    /// <summary>Whether what was measured was a healthy stream: the receiver's sessions, and the underruns and concealed
    /// gaps in the measured stretches (a benchmark of a stream breaking up measures the wrong thing).</summary>
    public int Sessions { get; init; }
    public long Underruns { get; init; }
    public long Concealed { get; init; }
    public long AudioPacketAllocations { get; init; }
    public long FormatPacketAllocations { get; init; }
    public long AudioPackets { get; init; }
    public long FormatPackets { get; init; }

    /// <summary>The spread of the measured windows around their median, as a fraction of it.</summary>
    public double Spread => Windows.Length < 2 || RawPercent <= 0 ? 0 : (Windows.Max() - Windows.Min()) / RawPercent;
}

/// <summary>
/// THE CPU BENCHMARK (2026-09-28). Each case runs the real sender(s) and receiver in the performance rig on a thread of its
/// own, and reads that thread's CPU time (<see cref="ThreadCpu"/>) over three measured stretches after a warm-up; the
/// median is the figure. The same case is then run again with every call into RemSound left out - the rig ticking its
/// cards, filling its blocks and carrying the same packets - and that is taken off, so the figure is RemSound's alone.
/// </summary>
internal static class PerfBench
{
    /// <summary>Simulated seconds before measuring (streams open, buffers fill, the drift loop settles its first window),
    /// then the measured stretches.</summary>
    public const double WarmSeconds = 1.5;
    public const double WindowSeconds = 2;
    public const int Windows = 3;

    public static string Name(SimConfig config) => config switch
    {
        SimConfig.WasapiOnly => "WASAPI only",
        SimConfig.AsioOnly => "ASIO only",
        _ => "both",
    };

    /// <summary>Everything the benchmarks will call, run once so the runtime has finished compiling it (a Release build
    /// compiles hot code twice: quickly, then properly a moment later) before any figure is taken.</summary>
    public static void WarmUp()
    {
        OnOwnThread(() =>
        {
            // The cycle counter's rate, measured by spinning, before any benchmark's stretch can be charged for the spin.
            _ = ThreadCpu.CyclesPerSecond;
            foreach (var config in new[] { SimConfig.WasapiOnly, SimConfig.AsioOnly, SimConfig.Both })
                foreach (var codec in new[] { SimCodec.Pcm, SimCodec.Opus2_5, SimCodec.Opus10 })
                {
                    using var rig = new PerfRig(new PerfRigOptions(config, codec));
                    rig.Join(0, PerfSignal.Busy);
                    var wall = Stopwatch.StartNew();
                    while (rig.Now < 3 || wall.Elapsed.TotalSeconds < 0.3) rig.RunFor(0.5);
                }
            return 0;
        });
    }

    public static PerfResult Run(PerfCase c)
    {
        var wall = Stopwatch.StartNew();
        var total = WarmSeconds + Windows * WindowSeconds;
        PerfTrace? recording = null;
        if (c.Part == PerfPart.ReceiverAlone)
        {
            // The receiver alone plays a sending end's packets, recorded first (not measured).
            recording = OnOwnThread(() =>
            {
                using var rig = new PerfRig(new PerfRigOptions(c.Config, c.Codec) { Part = PerfPart.SenderAlone, RecordTrace = true });
                for (var i = 0; i < c.Peers; i++) rig.Join(i, PerfSignal.Busy);
                rig.RunUntil(total + 0.5);
                return rig.Trace!;
            });
        }
        var options = new PerfRigOptions(c.Config, c.Codec)
        {
            Part = c.Part,
            Diagnostics = c.Diagnostics,
            RecordTrace = c.Part == PerfPart.Whole,
            TraceShapeOnly = true,
            Replay = recording,
        };
        var real = OnOwnThread(() => Measure(options, c, total));
        // The rig's own share: the same schedule, the same packets, no call into RemSound.
        var rigOptions = options with { RigOnly = true, RecordTrace = false, Replay = c.Part == PerfPart.Whole ? real.Trace : recording };
        var rigOnly = OnOwnThread(() => Measure(rigOptions, c, total));
        var rawMedian = Median(real.Windows);
        var rigMedian = Median(rigOnly.Windows);
        var measured = Windows * WindowSeconds;
        var meter = real.Meter;
        return new PerfResult(c, Math.Max(0, rawMedian - rigMedian), rawMedian, rigMedian, real.Windows,
            meter.Send / measured, meter.Receive / measured, meter.Render / measured, meter.EverySecond / measured,
            wall.Elapsed.TotalSeconds, real.TimesPercent)
        {
            Sessions = real.Sessions, Underruns = real.Underruns, Concealed = real.Concealed,
            AudioPacketAllocations = meter.AudioBytes, FormatPacketAllocations = meter.FormatBytes,
            AudioPackets = meter.AudioPackets, FormatPackets = meter.FormatPackets,
        };
    }

    private sealed record Measured(double[] Windows, PerfTrace? Trace, MeterTotals Meter, double TimesPercent, int Sessions, long Underruns, long Concealed);

    private sealed record MeterTotals(long Send, long Receive, long Render, long EverySecond, long AudioBytes, long FormatBytes, long AudioPackets, long FormatPackets);

    private static Measured Measure(PerfRigOptions options, PerfCase c, double total)
    {
        using var rig = new PerfRig(options);
        if (c.Part != PerfPart.ReceiverAlone)
            for (var i = 0; i < c.Peers; i++) rig.Join(i, PerfSignal.Busy);
        rig.RunUntil(WarmSeconds);
        rig.Meter.Reset();
        var sessions = rig.Receiver?.LiveSessionCountForTest ?? 0;
        var underruns0 = rig.Receiver?.Underruns ?? 0;
        var concealed0 = rig.Receiver?.ConcealmentFires ?? 0;
        var windows = new double[Windows];
        var times0 = ThreadCpu.TimesSeconds();
        for (var w = 0; w < Windows; w++)
        {
            var c0 = ThreadCpu.Cycles();
            rig.RunFor(WindowSeconds);
            var c1 = ThreadCpu.Cycles();
            windows[w] = ThreadCpu.ToSeconds(c1 - c0) / WindowSeconds * 100;
        }
        var timesPercent = (ThreadCpu.TimesSeconds() - times0) / (Windows * WindowSeconds) * 100;
        var m = rig.Meter;
        var totals = new MeterTotals(m.Allocated(PerfCost.Send),
            m.Allocated(PerfCost.DeliverAudio, PerfCost.DeliverFormat, PerfCost.DeliverOther),
            m.Allocated(PerfCost.Render, PerfCost.Producer), m.Allocated(PerfCost.EverySecond),
            m.Allocated(PerfCost.DeliverAudio), m.Allocated(PerfCost.DeliverFormat), m.Calls(PerfCost.DeliverAudio), m.Calls(PerfCost.DeliverFormat));
        var underruns = (rig.Receiver?.Underruns ?? 0) - underruns0;
        var concealed = (rig.Receiver?.ConcealmentFires ?? 0) - concealed0;
        // The rest of the recording is kept for the rig-only run, which plays the same packets.
        rig.RunUntil(total + 0.25);
        return new Measured(windows, rig.Trace, totals, timesPercent, sessions, underruns, concealed);
    }

    /// <summary>Run <paramref name="body"/> on a fresh thread of its own and wait for it: the thread whose CPU is read, with
    /// nothing else of the gate's on it, and the audio clock (per thread) its own.</summary>
    public static T OnOwnThread<T>(Func<T> body)
    {
        T result = default!;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            ThreadCpu.LastHold = ThreadCpu.HoldToFastestCores();
            // A core that has been idle runs slowly for its first few milliseconds while it speeds up, which is the whole
            // of a small benchmark's run (found 2026-09-28: one ran 70 % slow, all three stretches alike). Spun up first.
            var spin = Stopwatch.StartNew();
            while (spin.ElapsedMilliseconds < 40) { }
            try { result = body(); }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true, Name = "performance rig" };
        thread.Start();
        thread.Join();
        if (failure is not null) throw new InvalidOperationException($"the performance rig failed: {failure.GetType().Name}: {failure.Message}", failure);
        return result;
    }

    public static double Median(double[] values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        return sorted.Length == 0 ? 0 : sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
    }

    public static string Describe(PerfResult r) =>
        $"{r.Case.Plain}: {r.CorePercent:0.000} % of a core (measured {r.RawPercent:0.000}, the rig's own {r.RigPercent:0.000}; windows "
        + $"{string.Join("/", r.Windows.Select(w => w.ToString("0.000")))}; thread times say {r.TimesCrossCheckPercent:0.00}); allocations a second: "
        + $"sender {r.SendAllocPerSec:0}, receiving {r.ReceiveAllocPerSec:0}, playing {r.RenderAllocPerSec:0}, once-a-second work {r.EverySecondAllocPerSec:0} bytes; "
        + $"audio packets {r.AudioPackets} ({r.AudioPacketAllocations} bytes), format packets {r.FormatPackets} ({r.FormatPacketAllocations} bytes); "
        + $"{r.Sessions} session(s), {r.Underruns} underruns, {r.Concealed} concealed; {r.WallSeconds:0.00} s to run";
}
