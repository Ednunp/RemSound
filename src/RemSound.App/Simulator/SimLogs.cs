using System.Text.RegularExpressions;
using RemSound.Core;
using RemSound.Receiver;

namespace RemSound.App.Simulator;

/// <summary>What one simulated session did to the log.</summary>
internal sealed record SimLogResult(
    string Network, SimConfig Config, double Hours,
    long Gaps, int SecondsWithGaps, int PeerDrops, int Problems, int Bursts,
    int SecondsWritten, int MostSecondsInAnyHour, long Bytes, long MostBytesInAnyHour,
    bool InTimeOrder, bool EverySecondWhole, string Path)
{
    public double BytesPerHour => Hours > 0 ? Bytes / Hours : 0;
}

/// <summary>
/// THE LOG ON A REAL CONNECTION (2026-10-19). Ed, before releasing the once-a-minute log: "if logs are variable given
/// quality of connection ... could that variability make logs go all over the place".
///
/// <para>A simulated session over each modelled network, the real receiver hearing real audible gaps (its DropoutGaps,
/// counted as the app counts them in NoteAudibleGapsForTheLog), the main window's auto-tune running, and the real
/// <see cref="RemSoundLog"/> on the simulated clock taking the four lines the window makes every second. A peer can be
/// made to drop at set times (Jonathan's "my laptop kept disconnecting"). Then the file is read back: how big, how many
/// bursts, the busiest hour, and whether it stayed in time order with every written second whole.</para>
///
/// <para>The four lines are the app's in shape and close to it in length (the diagnostic line trimmed as it now is); their
/// numbers are this run's where the receiver has them and fixed elsewhere. Their length decides the size; the rule decides
/// which seconds are written, and that is the app's own code.</para>
/// </summary>
internal static class SimLogs
{
    /// <summary>The lines written for each second (diag, streams, latency-probe and the status row).</summary>
    public const int LinesPerSecond = 4;

    public static SimLogResult Run(SimNetwork network, SimConfig config, double seconds, int peerDropEverySec = 0, int seed = 1)
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "remsound-simlogs-" + Guid.NewGuid().ToString("N"));
        using var scope = AppConfig.UseThrowawayUserDataDirectory(dir);
        var start = new DateTime(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);
        var simNow = 0.0;
        long gapsSeen = -1, gaps = 0;
        int secondsWithGaps = 0, drops = 0;
        string? path;
        using (var log = new RemSoundLog { Enabled = true, UtcNowForTest = () => start.AddSeconds(simNow) })
        {
            void EverySecond(double now, AudioReceiver receiver)
            {
                simNow = now;
                var sec = (int)Math.Floor(now);
                // The app's order (SnapshotLogIfDue): a peer that went, then a new audible gap, then the second's lines.
                if (peerDropEverySec > 0 && sec % peerDropEverySec == 0)
                {
                    drops++;
                    log.Trouble("LAPTOP disconnected");
                }
                var total = receiver.DropoutGaps;
                var fresh = MainForm.UnderrunsThisSecond(total, gapsSeen);
                gapsSeen = total;
                if (fresh > 0)
                {
                    gaps += fresh;
                    secondsWithGaps++;
                    log.Trouble($"{fresh} audible gap{(fresh == 1 ? "" : "s")} [{config}]");
                }
                log.Detail(DiagLine(sec, receiver));
                log.Detail(StreamsLine(sec, config, fresh, total));
                log.Detail($"latency-probe sec={sec:D5} estimated one-way ≈ 27.9ms (capture=0.7[dev], send-accum=1.2[est], wire=0.5[meas], "
                         + $"recv-queue={receiver.CurrentBufferMs}[meas], out-queue=0.0[meas], render=3.6[dev])");
                log.Snapshot(true, true, true, "Opus", 10, 10, receiver.CurrentBufferMs, sec * 100L, sec * 64_000L,
                    $"Simulated capture sec={sec:D5}", receiver.PacketsReceived, receiver.BytesReceived, receiver.Underruns, receiver.Drops,
                    "Simulated output", "192.0.2.10: 1ms", 0, 0, 10, 10);
            }
            Simulation.Run(new SimScenario($"logs: {network.Name}", config, SimCodec.Opus10, network, 20, seconds, Seed: seed,
                ContinuousAutoTune: true, ChunkSeconds: 60, EverySecondDo: EverySecond));
            path = log.Path;
        }

        var text = path is null ? "" : ReadShared(path);
        var lines = text.Split('\n');
        var order = new List<int>();
        var perSecond = new Dictionary<int, int>();
        foreach (var line in lines)
        {
            var m = SecondPattern.Match(line);
            if (!m.Success) continue;
            var sec = int.Parse(m.Groups[1].Value);
            order.Add(sec);
            perSecond[sec] = perSecond.GetValueOrDefault(sec) + 1;
        }
        var bursts = lines.Count(l => l.Contains("\tdetail: ", StringComparison.Ordinal));
        var hours = seconds / 3600.0;
        // The busiest hour, by the seconds written in it and the bytes of their lines.
        var secondsByHour = order.Distinct().GroupBy(s => s / 3600).Select(g => g.Count()).DefaultIfEmpty(0).Max();
        var bytesByHour = lines.Select(l => (Match: SecondPattern.Match(l), l.Length))
            .Where(x => x.Match.Success).GroupBy(x => int.Parse(x.Match.Groups[1].Value) / 3600)
            .Select(g => g.Sum(x => (long)x.Length + 1)).DefaultIfEmpty(0).Max();
        return new SimLogResult(network.Name, config, hours, gaps, secondsWithGaps, drops, secondsWithGaps + drops, bursts,
            perSecond.Count, secondsByHour, text.Length, bytesByHour,
            InTimeOrder: order.Zip(order.Skip(1)).All(p => p.First <= p.Second),
            EverySecondWhole: perSecond.Values.All(n => n == LinesPerSecond),
            Path: path ?? "");
    }

    /// <summary>The most bursts an hour can hold under the rule: one, then one every ten minutes after it.</summary>
    public static int MostBurstsIn(double hours) => (int)Math.Floor(hours * 3600 / LogDetailRule.BurstGap.TotalSeconds) + 1;

    /// <summary>The most seconds a run can write under the rule: a minute line each minute, and every burst's before and
    /// after with one more for the second it began in.</summary>
    public static int MostSecondsIn(double hours, int bursts) =>
        (int)Math.Floor(hours * 3600 / LogDetailRule.Every.TotalSeconds) + 1
        + bursts * (int)(LogDetailRule.Before.TotalSeconds + LogDetailRule.After.TotalSeconds + 1);

    public static string Describe(SimLogResult r) =>
        $"{r.Network}, {r.Config}: {r.Hours * 60:0} min; {r.Gaps} audible gaps in {r.SecondsWithGaps} seconds, {r.PeerDrops} peer drops; "
        + $"{r.Bursts} written in full; {r.SecondsWritten} seconds written of {r.Hours * 3600:0}; "
        + $"{r.Bytes / 1024.0:0} KB, {r.BytesPerHour / 1024.0:0} KB an hour (busiest hour {r.MostBytesInAnyHour / 1024.0:0} KB); "
        + $"{(r.InTimeOrder ? "in time order" : "OUT OF ORDER")}, {(r.EverySecondWhole ? "every second whole" : "A SECOND SPLIT OR TWICE")}";

    private static readonly Regex SecondPattern = new(@"sec=(\d{5})", RegexOptions.Compiled);

    private static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs);
        return sr.ReadToEnd();
    }

    /// <summary>The diagnostic line as the window writes it since 2026-10-19 (no XB/WB split, no sampleStepMax).</summary>
    private static string DiagLine(int sec, AudioReceiver receiver) =>
        $"diag sec={sec:D5} bufAvg={receiver.CurrentBufferMs}ms bufMin=8ms bufMax=28ms maxGapMs=20 sendCbGapMs=1 renderCbGapMs=1 maxReadMs=0 reads=1520 "
        + "emitMs=0 sndCallMs=0 rxDispMs=0 rxNetGapMs=0 gc0Δ=1 gc1Δ=0 gc2Δ=0 cpu=0.0% memMB=3.6 wsMB=126.5 allocKBps=328.8 "
        + "privMB=56.6 gcHeapMB=19.3 gcFragMB=10.0 gcCommitMB=31.0 handles=820 threads=29 captureMs=22.2 sendMs=20.7 recvMs=0.0 renderMs=8.9 "
        + "renderWasapiMs=0.0 renderAsioMs=8.9 renderMixedMs=0.0 srcDrift=[] "
        + $"trimB=0 trimN=0 trimΔ=0 drainB=0 ovfB=0 pktRej=0 concealΔ=0 shortReadΔ=0 devGulpΔ=0 filtErr=830.8f capPeak=0.307 capPeakPlg=0.000 "
        + "emitGapWas=0 emitGapAsi=3 emitGapPlg=0 sndAudFrΔ=404 stepRawCap=0.068 stepPreEnc=0.068 stepPreEncWas=0.000 stepPreEncAsi=0.068 "
        + "stepPostDec=0.003 stepPostRing=0.003 stepPostRsm=0.003 clipΔ=0 spikesN=0 wireOkΔ=102 wireMissΔ=0 wireReordΔ=0 wireDupΔ=0 "
        + $"pcmRej=0 pcmDiscard=0 underruns={receiver.Underruns}";

    private static string StreamsLine(int sec, SimConfig config, long fresh, long total) =>
        $"streams sec={sec:D5} [{config}] [lowest-delay auto-tune off]: underruns this second ASIO={fresh} WASAPI=0; audible gaps so far "
        + $"ASIO={total} WASAPI=0; server path gap=20ms dispatch=0ms; ASIO lane sends the codec setting; peers asking for Jamulus-style=0 | "
        + "192.0.2.10:47830 stream=1 Mixed Opus 10ms pkts=101 gap=21ms gap-peak=31ms gap-used=28ms AsioLane: need=35ms own=- plays-at=5ms "
        + "output=5ms depth=19ms underruns=0";
}
