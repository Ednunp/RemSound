using RemSound.App.Simulator;
using RemSound.Core;

namespace RemSound.App;

internal static partial class SelfTest
{
    /// <summary>SIMULATOR BURSTS AND QUEUES (2026-09-30, the simulator review's fourth improvement). Networks as they really
    /// misbehave. Loss in bursts: the same half a percent lost, but in runs of about four packets, which the codec's
    /// repair of single losses cannot mend - heard, where one-at-a-time loss is not. A queue that builds and drains: the
    /// delay climbs to its peak and falls back, smoothly, every few seconds; a buffer below the peak breaks up, one above
    /// it plays clean and the delay comes back. And both draw no dice unless used: a network that names their shape but
    /// not their amount sounds sample for sample as it did.</summary>
    private static string SimulatorBurstsAndQueues()
    {
        // Known answers (2026-09-30): the internet preset, ASIO only, 2.5 ms Opus, 30 ms, 10 s, seed 1.
        const long KnownSent = 4040, KnownLost = 19, KnownWaits = 19;
        // 1. Loss in bursts against the same loss one at a time, at a buffer that hides lateness (60 ms).
        SimResult Lossy(SimNetwork net) => Simulation.Run(new SimScenario(net.Name, SimConfig.AsioOnly, SimCodec.Opus2_5, net, 60, 60, RecordNetwork: true));
        var single = Lossy(SimNetwork.Internet);
        var bursty = Lossy(SimNetwork.BurstyInternet);
        var (singleRuns, singleLost) = LossRuns(Require(single.Recording, "a recording"));
        var (burstRuns, burstLost) = LossRuns(Require(bursty.Recording, "a recording"));
        double Percent(SimResult r) => r.PacketsLost * 100.0 / r.PacketsSent;
        Check(Percent(single) is > 0.25 and < 1 && Percent(bursty) is > 0.2 and < 1.2,
            $"both networks must lose about half a percent ({Percent(single):0.00} % one at a time, {Percent(bursty):0.00} % in bursts)");
        var singleMean = singleLost / (double)Math.Max(1, singleRuns);
        var burstMean = burstLost / (double)Math.Max(1, burstRuns);
        Check(singleMean < 1.3 && burstMean is > 2.5 and < 6,
            $"one-at-a-time loss must come one at a time, burst loss in runs of about four ({singleMean:0.00} and {burstMean:0.00} packets a run)");
        Check(single.UnrecoveredGaps <= 2 && single.Findings.Glitches <= 1,
            $"single losses are repaired by the codec - {SimRunner.Describe(single)}");
        Check(bursty.UnrecoveredGaps > single.UnrecoveredGaps + 5 && bursty.Findings.QuietMs + bursty.Findings.StepClicks > single.Findings.QuietMs + single.Findings.StepClicks,
            $"bursts are beyond that repair and are heard - {SimRunner.Describe(bursty)}");

        // 2. A queue that builds and drains: 30 ms every 4 s or so, over 60 s.
        SimResult Queued(int buffer) => Simulation.Run(new SimScenario($"queue at {buffer} ms", SimConfig.AsioOnly, SimCodec.Opus2_5, SimNetwork.Congested, buffer, 60, RecordNetwork: true));
        var roomy = Queued(70);
        var replay = NetworkReplay.From(Require(roomy.Recording, "a recording"));
        var delays = Enumerable.Range(0, replay.Places).Select(p => replay.DelayOf(p) * 1000).ToArray();
        var peak = delays.Where(d => !double.IsNaN(d)).Max();
        Check(peak is > 28 and < 50, $"the queue must add about its 30 ms at the peak, on top of the path's own jitter ({peak:0.0} ms)");
        // Episodes: stretches where the delay is over half the peak, at least 100 ms apart.
        var episodes = 0;
        var lastHigh = -1000;
        var steps = new List<double>();
        for (var i = 0; i < delays.Length; i++)
        {
            if (double.IsNaN(delays[i]) || delays[i] < 15) continue;
            if (i - lastHigh > 40) episodes++;
            lastHigh = i;
        }
        Check(episodes is >= 6 and <= 30, $"over a minute the queue must fill about every four seconds ({episodes} times)");
        // It climbs and falls rather than jumping: the median delay over each 25 ms, from one to the next, never moves
        // by more than the climb's own pace allows (30 ms over 200 ms is 3.75 ms a 25 ms; twice that, for the jitter).
        var medians = new List<double>();
        for (var i = 0; i + 10 <= delays.Length; i += 10)
        {
            var window = delays.Skip(i).Take(10).Where(d => !double.IsNaN(d)).OrderBy(d => d).ToList();
            if (window.Count > 0) medians.Add(window[window.Count / 2]);
        }
        var biggestStep = medians.Zip(medians.Skip(1), (a, b) => Math.Abs(b - a)).Max();
        Check(biggestStep < 9, $"the delay must build and drain, not jump (the biggest move from one 25 ms to the next: {biggestStep:0.0} ms)");
        Check(roomy.Findings.Clean && roomy.Findings.DelayMaxMs - roomy.Findings.DelayMinMs < 6,
            $"a buffer above the peak must play clean, the delay holding within a few milliseconds through every fill - {SimRunner.Describe(roomy)}");
        var tight = Queued(15);
        Check(tight.Findings.Glitches >= episodes / 3 && !tight.Findings.Clean,
            $"a buffer below the peak must break up as the queue fills - {SimRunner.Describe(tight)}");

        // 3. No dice unless used: the internet preset, and the same naming the bursts' length and the queue's shape but no
        // amount of either - sample for sample the same.
        var inert = SimNetwork.Internet with { BurstLossLength = 9, QueueEverySec = 1, QueueRiseMs = 50, QueueFallMs = 50 };
        var a = Simulation.Run(new SimScenario("as it was", SimConfig.AsioOnly, SimCodec.Opus2_5, SimNetwork.Internet, 30, 10, KeepAudio: true));
        var b = Simulation.Run(new SimScenario("shape only", SimConfig.AsioOnly, SimCodec.Opus2_5, inert, 30, 10, KeepAudio: true));
        Check(Require(a.Audio, "the audio").AsSpan().SequenceEqual(Require(b.Audio, "the audio")) && a.PacketsLost == b.PacketsLost,
            "a network with no bursts and no queue must sound sample for sample as it did, whatever shape it names for them");
        // And as it always has: the dice of the internet preset, seed 1, as they fell before bursts and queues existed.
        Check(a.PacketsSent == KnownSent && a.PacketsLost == KnownLost && a.ReorderWaits == KnownWaits,
            $"the internet preset's dice must fall as they always have: {KnownSent} packets, {KnownLost} lost, {KnownWaits} reorder waits in 10 s "
            + $"({a.PacketsSent}, {a.PacketsLost}, {a.ReorderWaits})");

        return $"half a percent lost: one at a time ({singleMean:0.0} a run) {single.UnrecoveredGaps} gaps not repaired, in bursts ({burstMean:0.0} a run) "
             + $"{bursty.UnrecoveredGaps}; a queue filling {episodes} times a minute to {peak:0.0} ms, clean at 70 ms and {tight.Findings.Glitches} break-ups at 15 ms";

        // The runs of sequence numbers that never came, in the recording's first stream: how many runs, how many packets.
        static (int Runs, int Lost) LossRuns(NetworkRecording rec)
        {
            var seen = rec.Packets.Where(p => p.Stream == 0).Select(p => p.Sequence).Distinct().OrderBy(s => s).ToList();
            int runs = 0, lost = 0;
            for (var i = 1; i < seen.Count; i++)
            {
                var gap = (int)(seen[i] - seen[i - 1]) - 1;
                if (gap <= 0) continue;
                runs++;
                lost += gap;
            }
            return (runs, lost);
        }
    }
}
