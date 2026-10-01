using System.Diagnostics;

namespace RemSound.App.Simulator;

/// <summary>
/// A soak's day. Peer 0 is on a call from the start for <paramref name="CallHours"/> (long enough to carry its stream past
/// the receiver's ring positions wrapping, which a ring's 2^31 bytes do after 1.55 hours of audio), and again for the last
/// <paramref name="EveningCallMinutes"/>, whose last <paramref name="ListenSeconds"/> are listened to. Peer 1 visits for
/// <paramref name="VisitSeconds"/> of every <paramref name="VisitEveryMinutes"/>, all day, so sessions open and are pruned
/// dozens of times. The outputs stay open all day, as the app keeps them. The process and every rig's tables are looked at
/// every <paramref name="SampleEveryMinutes"/>, at a moment no visitor is in.
/// </summary>
/// <param name="Hours">Simulated hours, the same for each configuration (they run side by side, a thread each).</param>
/// <param name="AsioFrames">The ASIO cards' buffer. 256 frames (5.3 ms) is a common real setting; the soak is about what
/// builds up over hours, which the buffer size does not change, and a smaller one only costs more time to run.</param>
/// <param name="WrapWireCountersAfterPackets">Peer 0's wire counters start this many packets short of 2^32, so they wrap
/// minutes into the call rather than 33 to 241 days in (0: from 0, as a new sender does).</param>
internal sealed record SoakOptions(double Hours, SimCodec Codec = SimCodec.Pcm, int AsioFrames = 256, double SampleEveryMinutes = 30,
    double CallHours = 2, double EveningCallMinutes = 2, double VisitEveryMinutes = 30, double VisitSeconds = 60,
    uint WrapWireCountersAfterPackets = 50_000, double ListenSeconds = 20, SimConfig[]? Configs = null, bool WasapiOutputStage = true)
{
    public SimConfig[] ConfigsOrAll => Configs ?? [SimConfig.WasapiOnly, SimConfig.AsioOnly, SimConfig.Both];
    public double TotalSeconds => Hours * 3600;
    public double CallEnd => Math.Min(CallHours * 3600, TotalSeconds);
    public double EveningStart => Math.Max(CallEnd, TotalSeconds - EveningCallMinutes * 60);
    public double ListenFrom => TotalSeconds - ListenSeconds;
    /// <summary>Visits start two thirds of the way into each period, so every sample (on the period's boundaries, when the
    /// sample period is the visit period) comes long after the last visitor's session was pruned.</summary>
    public double VisitOffset => VisitEveryMinutes * 60 * 2 / 3;

    public bool CallAt(double t) => t < CallEnd || t >= EveningStart;

    public bool VisitAt(double t)
    {
        var period = VisitEveryMinutes * 60;
        var into = t % period;
        return into >= VisitOffset && into < VisitOffset + VisitSeconds;
    }

    /// <summary>The next moment after <paramref name="t"/> that anything in the day changes.</summary>
    public double NextChange(double t)
    {
        var period = VisitEveryMinutes * 60;
        var start = t - t % period;
        var candidates = new[]
        {
            CallEnd, EveningStart, ListenFrom, TotalSeconds,
            start + VisitOffset, start + VisitOffset + VisitSeconds, start + period + VisitOffset,
        };
        return candidates.Where(c => c > t + 1e-9).DefaultIfEmpty(TotalSeconds).Min();
    }
}

/// <summary>One look at the process and at each configuration's rig, part way through a soak.</summary>
internal sealed record SoakSample(double Hours, bool Call, ProcessState Process,
    IReadOnlyDictionary<SimConfig, IReadOnlyList<(string Table, long Count)>> Tables, IReadOnlyDictionary<SimConfig, SoakCounters> Counters,
    IReadOnlyDictionary<SimConfig, double> CpuPercent);

/// <summary>A configuration's running totals: what went across, and everything that would say something went wrong.</summary>
internal readonly record struct SoakCounters(long PacketsReceived, long BytesReceived, long PacketsSent, long BytesSent, long Underruns,
    long Concealed, long LateDropped, long Restarts, long Fec, long Unrecovered, long PcmConcealed, long Trims, long Drops,
    long SessionsOpened, long Lost, int RigPacketBuffers, int RigQueuedEvents)
{
    /// <summary>Audio written into the open sessions' rings (a ring's positions wrap at 2^31 bytes).</summary>
    public long RingBytesWritten { get; init; }
}

/// <summary>What a soak found: every sample, what the evening call sounded like, and the wall time.</summary>
internal sealed record SoakResult(SoakOptions Options, IReadOnlyList<SoakSample> Samples, IReadOnlyDictionary<SimConfig, SimAnalysis.Findings> Heard,
    IReadOnlyDictionary<SimConfig, string> Failures, double WallSeconds, ProcessState Before, ProcessState After,
    IReadOnlyDictionary<SimConfig, SoakCounters> CallCounters);

/// <summary>
/// THE SOAK (2026-09-28). The three configurations each live a simulated day (or days) in the performance rig, side by side
/// on a thread each; at every sample all three stop at the same simulated moment while the process is looked at - the
/// managed heap after a full collection, the handles, the threads - with each rig's receiver and sender tables, entry by
/// entry, and each rig thread's own CPU for the stretch. See <see cref="SoakOptions"/> for the day.
/// </summary>
internal static class SimSoak
{
    /// <summary>The most distinct tables a rig reports (the receiver's, the engine's, the senders' and the rig's own).</summary>
    private const int MaxTables = 64;

    public static SoakResult Run(SoakOptions o, Action<string>? progress = null)
    {
        var configs = o.ConfigsOrAll;
        var wall = Stopwatch.StartNew();
        var chunkSeconds = o.SampleEveryMinutes * 60;
        var chunks = (int)Math.Ceiling(o.TotalSeconds / chunkSeconds);
        using var barrier = new Barrier(configs.Length + 1);
        // Everything a sample records goes into storage made before the run starts: a record kept per sample would itself
        // be heap that grows through the day, which is what the soak is watching for (found 2026-09-28: 6 KB an hour).
        var states = new ProcessState[chunks];
        var cpu = new double[configs.Length, chunks];
        var counters = new SoakCounters?[configs.Length, chunks];
        var counts = new long[configs.Length, chunks, MaxTables];
        var tableIndex = configs.Select(_ => new Dictionary<string, int>(MaxTables, StringComparer.Ordinal)).ToArray();
        var heard = new Dictionary<SimConfig, SimAnalysis.Findings>();
        var failures = new Dictionary<SimConfig, string>();
        var callCounters = new Dictionary<SimConfig, SoakCounters>();
        var gate = new object();
        var before = ProcessState.Sample();

        var threads = configs.Select((config, ci) => new Thread(() =>
        {
            PerfRig? rig = null;
            var failed = false;
            try
            {
                ThreadCpu.HoldToFastestCores();
                rig = new PerfRig(new PerfRigOptions(config, o.Codec) { AsioFrames = o.AsioFrames, Seed = 11, WasapiOutputStage = o.WasapiOutputStage });
            }
            catch (Exception ex) { failed = true; lock (gate) failures[config] = $"could not start: {ex.GetType().Name}: {ex.Message}"; }
            int caller = -1, visitor = -1;
            var listening = false;
            for (var chunk = 1; chunk <= chunks; chunk++)
            {
                try
                {
                    if (rig is not null && !failed)
                    {
                        var end = Math.Min(chunk * chunkSeconds, o.TotalSeconds);
                        var c0 = ThreadCpu.Cycles();
                        var t0 = rig.Now;
                        while (rig.Now < end)
                        {
                            var t = rig.Now;
                            if (o.CallAt(t) && caller < 0)
                            {
                                caller = rig.Join(0, PerfSignal.ToneAndClicks);
                                if (t == 0 && o.WrapWireCountersAfterPackets > 0)
                                    foreach (var sender in rig.Senders) sender.StartWireCountersAtForTest(uint.MaxValue - o.WrapWireCountersAfterPackets);
                            }
                            if (!o.CallAt(t) && caller >= 0)
                            {
                                // The call's own figures, while its session is still open (a pruned session takes its counts with it).
                                lock (gate) callCounters[config] = CountersOf(rig);
                                rig.Leave(caller);
                                caller = -1;
                            }
                            if (o.VisitAt(t) && visitor < 0) visitor = rig.Join(1, PerfSignal.Silence);
                            if (!o.VisitAt(t) && visitor >= 0) { rig.Leave(visitor); visitor = -1; }
                            if (!listening && t >= o.ListenFrom) { rig.StartListening(o.ListenSeconds + 1); listening = true; }
                            rig.RunUntil(Math.Min(end, o.NextChange(t)));
                        }
                        cpu[ci, chunk - 1] = ThreadCpu.ToSeconds(ThreadCpu.Cycles() - c0) / Math.Max(1e-9, rig.Now - t0) * 100;
                        if (chunk == chunks && listening) lock (gate) heard[config] = rig.StopListening();
                        // A call running to the end (the overnight mode's one long call) has its figures taken here.
                        if (chunk == chunks && caller >= 0) lock (gate) callCounters.TryAdd(config, CountersOf(rig));
                        counters[ci, chunk - 1] = CountersOf(rig);
                        foreach (var (table, count) in TablesOf(rig))
                        {
                            if (!tableIndex[ci].TryGetValue(table, out var j))
                            {
                                if (tableIndex[ci].Count >= MaxTables) continue;
                                j = tableIndex[ci].Count;
                                tableIndex[ci][table] = j;
                            }
                            counts[ci, chunk - 1, j] = counts[ci, chunk - 1, j] + count;   // two senders' tables add up
                        }
                    }
                }
                catch (Exception ex) { failed = true; lock (gate) failures[config] = $"{ex.GetType().Name}: {ex.Message} at {rig?.Now / 3600:0.00} h"; }
                barrier.SignalAndWait();   // this stretch done
                barrier.SignalAndWait();   // the process has been looked at
            }
            try { rig?.Dispose(); } catch { /* teardown */ }
        }) { IsBackground = true, Name = $"soak {config}" }).ToList();

        threads.ForEach(t => t.Start());
        for (var chunk = 1; chunk <= chunks; chunk++)
        {
            barrier.SignalAndWait();
            states[chunk - 1] = ProcessState.Sample();
            if (progress is not null)
            {
                var at = Math.Min(chunk * chunkSeconds, o.TotalSeconds);
                progress($"{at / 3600:0.0} h: {states[chunk - 1]}; {string.Join(", ", configs.Select((c, ci) => $"{PerfBench.Name(c)} {cpu[ci, chunk - 1]:0.00} %"))} "
                    + $"of a core; {wall.Elapsed.TotalSeconds:0} s so far");
            }
            barrier.SignalAndWait();
        }
        threads.ForEach(t => t.Join());
        var after = ProcessState.Sample();

        // Only now, with every measurement taken, are the samples built.
        var samples = new List<SoakSample>(chunks);
        for (var k = 0; k < chunks; k++)
        {
            var at = Math.Min((k + 1) * chunkSeconds, o.TotalSeconds);
            var tables = new Dictionary<SimConfig, IReadOnlyList<(string Table, long Count)>>();
            var sampleCounters = new Dictionary<SimConfig, SoakCounters>();
            var sampleCpu = new Dictionary<SimConfig, double>();
            for (var ci = 0; ci < configs.Length; ci++)
            {
                var index = ci;
                var sample = k;
                tables[configs[ci]] = tableIndex[ci].Select(kv => (kv.Key, counts[index, sample, kv.Value])).ToList();
                if (counters[ci, k] is { } c) sampleCounters[configs[ci]] = c;
                sampleCpu[configs[ci]] = cpu[ci, k];
            }
            // A call sample: the moment ends a stretch the call ran through (its session still open).
            samples.Add(new SoakSample(at / 3600, o.CallAt(at - 1), states[k], tables, sampleCounters, sampleCpu));
        }
        return new SoakResult(o, samples, heard, failures, wall.Elapsed.TotalSeconds, before, after, callCounters);
    }

    private static IReadOnlyList<(string Table, long Count)> TablesOf(PerfRig rig)
    {
        var list = new List<(string Table, long Count)>();
        if (rig.Receiver is { } receiver) list.AddRange(receiver.TablesForTest());
        foreach (var sender in rig.Senders) list.AddRange(sender.TablesForTest());
        list.Add(("Jamulus-style peers remembered (machine-wide)", RemSound.Core.CustomOpusPeers.EntriesForTest));
        var (buffers, events) = rig.RigSize;
        list.Add(("the rig's own packet buffers", buffers));
        list.Add(("the rig's own queued events", events));
        return list;
    }

    private static SoakCounters CountersOf(PerfRig rig)
    {
        var r = rig.Receiver!;
        var (fec, unrecovered, _) = r.CodecRepairsForTest;
        var (buffers, events) = rig.RigSize;
        return new SoakCounters(r.PacketsReceived, r.BytesReceived, rig.Senders.Sum(s => s.PacketsSent), rig.Senders.Sum(s => s.BytesSent),
            r.TuneBlockingUnderruns, r.ConcealmentFires, r.LateDroppedForTest, r.SequenceRestartsForTest, fec, unrecovered, r.PcmConcealedForTest,
            r.TrimFireCount, r.Drops, r.SessionsOpenedCount, rig.Lost, buffers, events) { RingBytesWritten = r.AudioBytesDecodedForTest };
    }

    /// <summary>The least-squares line through (x, y): its slope.</summary>
    public static double Slope(IReadOnlyList<(double X, double Y)> points)
    {
        if (points.Count < 2) return 0;
        var mx = points.Average(p => p.X);
        var my = points.Average(p => p.Y);
        var den = points.Sum(p => (p.X - mx) * (p.X - mx));
        return den <= 0 ? 0 : points.Sum(p => (p.X - mx) * (p.Y - my)) / den;
    }
}
