using System.Diagnostics;
using System.Net;
using NAudio.Wave;
using RemSound.Core;
using RemSound.Receiver;
using RemSound.Sender;

namespace RemSound.App.Simulator;

/// <summary>A master metronome and one follower, simulated: two machines whose clocks and sound cards drift apart, an
/// asymmetric jittery network between them, the outputs of a configuration on each.</summary>
/// <param name="FollowerClockPpm">How fast the follower's clock runs against the master's (its Stopwatch), + is fast.</param>
/// <param name="AsymmetryMs">How much longer the way from the master to the follower takes than the way back.</param>
/// <param name="CallbackJitterMs">How late each output's callback can come (its thread woken late); the card itself keeps
/// perfect time.</param>
internal sealed record SimMetronomeScenario(
    SimConfig Config,
    double Seconds = 60,
    double OffsetMs = 1234.567,
    double FollowerClockPpm = 50,
    double MasterCardPpm = 30,
    double FollowerCardPpm = -40,
    double NetBaseMs = 5,
    double NetJitterMs = 1,
    double AsymmetryMs = 2,
    double CallbackJitterMs = 0.5,
    double StartAtSec = 5,
    int Seed = 1);

/// <summary>What was heard, and what was sent.</summary>
/// <param name="WorstMs">The largest difference, over every beat both heard, between the moment the master's output and the
/// follower's output played it (in true time). Per pair of outputs of the same kind, and between the two outputs of one
/// machine in "both".</param>
/// <param name="Unmatched">Beats one side played and the other did not, from the follower's first click on.</param>
/// <param name="SentSamples">What the two machines sent, sample for sample, checked against what they captured: the tone
/// alone (the output they send a loopback of never carries a click).</param>
internal sealed record SimMetronomeResult(
    SimMetronomeScenario Scenario,
    double WorstMs,
    double WorstOnceRateKnownMs,
    double MedianMs,
    int BeatsCompared,
    int Unmatched,
    long SentSamples,
    long SentMismatches,
    int LoopedOutputClicks,
    string Estimate,
    IReadOnlyList<string> Outputs)
{
    public string EstimateErrorAll { get; init; } = "";
    public string EstimateErrorOnceRateKnown { get; init; } = "";

    public override string ToString() =>
        $"{Scenario.Config}: {BeatsCompared} beats compared on {string.Join(", ", Outputs)}, worst {WorstMs:0.000} ms ({WorstOnceRateKnownMs:0.000} ms once the rate is known), median {MedianMs:0.000} ms, "
        + $"{Unmatched} unmatched; sent {SentSamples} samples, {SentMismatches} not the capture; the looped output clicked {LoopedOutputClicks} times; follower's estimate {Estimate}, "
        + $"its error over the run {EstimateErrorAll} ({EstimateErrorOnceRateKnown} once the rate is known)";
}

/// <summary>
/// THE METRONOME, SIMULATED (PLAN-metronome-2026-09-28, "The simulator (measure, don't reason)"). Two machines in virtual
/// time, each with the real metronome engine on its own clock, the real sealed type-12 messages over a simulated network
/// with jitter and a path slower one way than the other, and the real output code on each machine's sound cards: the ASIO
/// output's broadcast by its stated delay, and the WASAPI stage its card pulls through, with the card saying what it has
/// played. The master starts; every click is found in what each card played, and the moment it was heard is compared.
///
/// <para>And nothing is ever sent: each machine sends its capture - a tone, plus a loopback of a WASAPI output of its own
/// that the profile sends - and what goes out is checked against the tone, sample by sample. The looped output is the one
/// the click is kept off; another WASAPI output beside it clicks.</para>
/// </summary>
internal static class SimMetronome
{
    private const int Rate = 48000;

    private sealed class Output(string name, bool asio, double period, double cardRate)
    {
        public string Name { get; } = name;
        public bool Asio { get; } = asio;
        public double Period { get; } = period;
        public double CardRate { get; } = cardRate;
        public IWaveProvider Provider = null!;
        public MultiOutputPlayout.SimulatedStage? Stage;
        public readonly List<double> Onsets = [];
        public int QuietFrames = Rate;
        public readonly Queue<float> Played = new();
        public readonly Queue<(double Start, double Seconds)> Playing = new();
        public double PlayedDone;

        public double PlayedAt(double t)
        {
            while (Playing.Count > 0 && Playing.Peek().Start + Playing.Peek().Seconds <= t) PlayedDone += Playing.Dequeue().Seconds;
            return Playing.Count > 0 && t > Playing.Peek().Start ? PlayedDone + (t - Playing.Peek().Start) : PlayedDone;
        }
    }

    private sealed class Machine
    {
        public required string Name;
        public required MetronomeEngine Engine;
        public required Func<long> Clock;
        public required AudioReceiver Receiver;
        public required AudioSender Sender;
        public required IPEndPoint At;
        public readonly List<Output> Outputs = [];
        public Output? Looped;
        public readonly MetronomeReplayGuard Guard = new();
        public long Captured;
        public readonly List<float> Tone = [];
        public readonly List<float> Sent = [];
    }

    public static SimMetronomeResult Run(SimMetronomeScenario s)
    {
        var rng = new Random(s.Seed);
        double now = 0;
        var baseTicks = Stopwatch.GetTimestamp();
        AudioClock.UseSimulated(() => baseTicks + (long)(now * Stopwatch.Frequency));
        var queue = new PriorityQueue<Action, (double, long)>();
        long order = 0;
        void At(double t, Action a) => queue.Enqueue(a, (t, order++));
        var (maybeKey, fingerprint) = RemSoundCrypto.ForPlainPassword("simulated metronome");
        var key = maybeKey ?? throw new InvalidOperationException("the password gave no key");
        var sound = MetronomeSoundLibrary.Load("Click") ?? throw new InvalidOperationException("the Click sound did not load");
        const long LocalBase = 5_000_000_000;
        var machines = new List<Machine>();
        try
        {
            Machine Make(string name, IPEndPoint at, Func<long> clock, double cardPpm, bool follower)
            {
                var receiver = new AudioReceiver();
                receiver.SetOutputDevices([]);
                receiver.SetActiveOutputLanes(wasapiActive: s.Config != SimConfig.AsioOnly, asioActive: s.Config != SimConfig.WasapiOnly);
                var sender = new AudioSender();
                sender.SetReceivers([new IPEndPoint(IPAddress.Parse("10.88.0.99"), 47830)]);
                sender.AudioKey = key;
                sender.AudioFingerprint = fingerprint;
                Simulation.ConfigureCodec(sender, SimCodec.Pcm);
                sender.UseDefaultLaneForTest(RenderRoute.Mixed);
                sender.PacketTapForTest = (_, _) => { };   // the network under test is the metronome's; audio goes nowhere
                var engine = new MetronomeEngine(clock);
                engine.Configure(new MetronomeSettings { Sound = "Click", WhenAsked = follower ? MetronomeMasterAnswer.AcceptAutomatically : MetronomeMasterAnswer.Ask }, sound);
                var m = new Machine { Name = name, Engine = engine, Clock = clock, Receiver = receiver, Sender = sender, At = at };
                sender.OnSentSamples = (samples, _) => { foreach (var f in samples.Span) m.Sent.Add(f); };
                engine.SchedulePublished = schedule => receiver.Metronome.Schedule = schedule;
                var cardRate = Rate * (1 + cardPpm * 1e-6);
                if (s.Config != SimConfig.WasapiOnly)
                {
                    var o = new Output($"{name} ASIO", asio: true, 32 / cardRate, cardRate);
                    o.Provider = AsioRenderBackend.BroadcastForTest(receiver.SimulationOutput(RenderRoute.AsioLane), 2, receiver.Metronome, 32 * 1000.0 / Rate);
                    m.Outputs.Add(o);
                }
                if (s.Config != SimConfig.AsioOnly)
                {
                    var route = s.Config == SimConfig.WasapiOnly ? RenderRoute.Mixed : RenderRoute.WasapiLane;
                    foreach (var (label, looped) in new[] { ("WASAPI", false), ("looped WASAPI", true) })
                    {
                        var o = new Output($"{name} {label}", asio: false, 480 / cardRate, cardRate);
                        var stage = new MultiOutputPlayout.SimulatedStage(o.Name, null, receiver.Metronome, o.Name) { StatedLatencyMs = 20 };
                        stage.PlayedSeconds = () => o.PlayedAt(now);
                        o.Stage = stage;
                        o.Provider = stage;
                        m.Outputs.Add(o);
                        if (looped) m.Looped = o;
                        // The producer that feeds it, 10 ms of the engine a tick on the machine's timer.
                        var source = receiver.SimulationOutput(route);
                        var frame = new byte[MultiOutputPlayout.SimulatedStage.ProducerFrameBytes];
                        for (var k = 1; k * 0.010 < s.Seconds; k++)
                            At(k * 0.010 + rng.NextDouble() * 0.001, () => { var n = source.Read(frame, 0, frame.Length); if (n > 0) stage.Feed(frame, 0, n); });
                    }
                    receiver.Metronome.SetLoopbackSent([m.Looped!.Name]);
                }
                machines.Add(m);
                return m;
            }

            var master = Make("master", new IPEndPoint(IPAddress.Parse("10.88.0.1"), 47830), () => (long)Math.Round(now * 1e6) + LocalBase, s.MasterCardPpm, follower: false);
            var follower = Make("follower", new IPEndPoint(IPAddress.Parse("10.88.0.2"), 47830),
                () => (long)Math.Round(now * 1e6 * (1 + s.FollowerClockPpm * 1e-6)) + LocalBase + (long)Math.Round(s.OffsetMs * 1000), s.FollowerCardPpm, follower: true);
            master.Engine.Peers = () => [(follower.At, "follower")];
            follower.Engine.Peers = () => [(master.At, "master")];

            // The network: sealed on the way out, opened (and a replay refused) on the way in; the master's way slower.
            uint sequence = 0;
            double Delay(bool fromMaster) =>
                (s.NetBaseMs + (fromMaster ? s.AsymmetryMs : 0) + Math.Min(-Math.Log(1 - rng.NextDouble()) * s.NetJitterMs, 6 * s.NetJitterMs)) / 1000;
            void Wire(Machine from, Machine to)
            {
                from.Engine.Send = (message, target) =>
                {
                    var packet = MetronomeWire.BuildPacket(key, message, ++sequence);
                    At(now + Delay(from == master), () =>
                    {
                        var stamp = to.Clock();
                        if (!to.Guard.TryAccept(key, packet.AsSpan(RemPacket.HeaderSize), out var m, out _) || m is null) return;
                        if (m.Kind == MetronomeKind.SyncRequest)
                        {
                            if (to.Engine.TryAnswerSync(m, from.At, stamp) is { } reply) to.Engine.Send!(reply with { T2 = to.Clock() }, from.At);
                        }
                        else to.Engine.OnMessage(m, from.At, stamp);
                    });
                };
            }
            Wire(master, follower);
            Wire(follower, master);

            // Each engine's 50 ms timer, on its machine; and the follower's estimate against the truth, once settled.
            var estimateErrors = new List<(double At, double Ms)>();
            for (var k = 1; k * 0.050 < s.Seconds; k++)
            {
                At(k * 0.050 + rng.NextDouble() * 0.002, master.Engine.Tick);
                At(k * 0.050 + rng.NextDouble() * 0.002, () =>
                {
                    follower.Engine.Tick();
                    if (follower.Engine.FollowerReady)
                        estimateErrors.Add((now, (follower.Engine.Estimator.Map.LocalToMaster(follower.Clock()) - master.Clock()) / 1000.0));
                });
            }
            At(0.5, () => master.Engine.BecomeMaster("master"));
            At(s.StartAtSec, master.Engine.Start);

            // The output cards: each callback on its card's own clock (up to CallbackJitterMs late), heard one buffer later.
            foreach (var m in machines)
                foreach (var o in m.Outputs)
                {
                    var frames = o.Asio ? 32 : 480;
                    var bytes = new byte[frames * (o.Asio ? 2 : 2) * sizeof(float)];
                    var output = o;
                    for (var k = 1; k * o.Period < s.Seconds; k++)
                    {
                        var nominal = k * o.Period;
                        At(nominal + rng.NextDouble() * s.CallbackJitterMs / 1000, () =>
                        {
                            Array.Clear(bytes);
                            output.Provider.Read(bytes, 0, bytes.Length);
                            // Heard from one card buffer after its nominal moment, at the card's own rate.
                            output.Playing.Enqueue((nominal + output.Period, frames / (double)Rate));
                            var floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(bytes);
                            for (var f = 0; f < frames; f++)
                            {
                                var v = Math.Max(Math.Abs(floats[2 * f]), Math.Abs(floats[2 * f + 1]));
                                if (v >= 0.05f && output.QuietFrames >= Rate / 20) output.Onsets.Add(nominal + output.Period + f / output.CardRate);
                                output.QuietFrames = v < 0.01f ? output.QuietFrames + 1 : 0;
                                if (output == m.Looped) { output.Played.Enqueue(floats[2 * f]); output.Played.Enqueue(floats[2 * f + 1]); }
                            }
                        });
                    }
                }

            // Each machine's capture: a tone in 10 ms blocks, plus the loopback of the output its profile sends; out it goes.
            foreach (var m in machines)
            {
                var machine = m;
                for (var k = 1; k * 0.010 < s.Seconds; k++)
                    At(k * 0.010 + 0.0003, () =>
                    {
                        var block = new float[480 * 2];
                        for (var i = 0; i < 480; i++)
                        {
                            var v = (float)(0.25 * Math.Sin(2 * Math.PI * 440 * (machine.Captured + i) / Rate));
                            block[2 * i] = block[2 * i + 1] = v;
                            machine.Tone.Add(v);
                            machine.Tone.Add(v);
                        }
                        machine.Captured = machine.Captured + 480;   // (a plain sum: "x.y += " reads as a handler to the event guard)
                        if (machine.Looped is { } looped)
                            for (var i = 0; i < block.Length && looped.Played.Count > 0; i++) block[i] += looped.Played.Dequeue();
                        machine.Sender.FeedDefaultLaneForTest(block);
                    });
            }

            while (queue.TryDequeue(out var action, out var when))
            {
                if (when.Item1 > s.Seconds) break;
                now = when.Item1;
                action();
            }

            // What was heard: the master's and the follower's outputs of the same kind, beat by beat, from the follower's
            // first click on; and the two outputs of one machine in "both".
            var diffs = new List<double>();
            var diffsOnceRateKnown = new List<double>();
            // The rate is fitted from 16 s of clock readings; the follower starts reading at half a second.
            var rateKnownFrom = 0.5 + 2 * MetronomeClockEstimator.BlockUs / 1e6 + MetronomeClockEstimator.BlockUs / 1e6;
            var unmatched = 0;
            var described = new List<string>();
            void Compare(Output a, Output b)
            {
                if (a.Onsets.Count == 0 || b.Onsets.Count == 0) { unmatched += a.Onsets.Count + b.Onsets.Count + 1; return; }
                var from = Math.Max(a.Onsets[0], b.Onsets[0]) - 0.1;
                var until = Math.Min(a.Onsets[^1], b.Onsets[^1]) + 0.1;
                var aa = a.Onsets.Where(t => t >= from && t <= until).ToList();
                var bb = b.Onsets.Where(t => t >= from && t <= until).ToList();
                foreach (var t in aa)
                {
                    var nearest = bb.MinBy(x => Math.Abs(x - t));
                    if (Math.Abs(nearest - t) > 0.050) unmatched++;
                    else
                    {
                        diffs.Add(Math.Abs(nearest - t) * 1000);
                        if (t >= rateKnownFrom) diffsOnceRateKnown.Add(Math.Abs(nearest - t) * 1000);
                    }
                }
                if (aa.Count != bb.Count) unmatched += Math.Abs(aa.Count - bb.Count);
                described.Add($"{a.Name} / {b.Name} ({aa.Count} beats)");
            }
            foreach (var mo in master.Outputs.Where(o => o != master.Looped))
            {
                var fo = follower.Outputs.First(o => o != follower.Looped && o.Asio == mo.Asio);
                Compare(mo, fo);
            }
            if (s.Config == SimConfig.Both)
                foreach (var m in machines) Compare(m.Outputs.First(o => o.Asio), m.Outputs.First(o => !o.Asio && o != m.Looped));

            // What was sent: sample by sample, the capture's tone and nothing else.
            long sentChecked = 0, mismatches = 0;
            foreach (var m in machines)
            {
                var n = Math.Min(m.Sent.Count, m.Tone.Count);
                for (var i = 0; i < n; i++, sentChecked++) if (Math.Abs(m.Sent[i] - m.Tone[i]) > 1e-6f) mismatches++;
                if (m.Sent.Count < m.Tone.Count - 480 * 2 * 4) mismatches += m.Tone.Count - m.Sent.Count;
            }
            var loopedClicks = machines.Sum(m => m.Looped?.Onsets.Count ?? 0);
            var sorted = diffs.OrderBy(d => d).ToList();
            return new SimMetronomeResult(s, sorted.Count == 0 ? double.NaN : sorted[^1], diffsOnceRateKnown.Count == 0 ? double.NaN : diffsOnceRateKnown.Max(),
                sorted.Count == 0 ? double.NaN : sorted[sorted.Count / 2],
                sorted.Count, unmatched, sentChecked, mismatches, loopedClicks, follower.Engine.Estimator.Describe(),
                described)
            {
                EstimateErrorAll = estimateErrors.Count == 0 ? "none" : $"{estimateErrors.Min(e => e.Ms):+0.000;-0.000} to {estimateErrors.Max(e => e.Ms):+0.000;-0.000} ms",
                EstimateErrorOnceRateKnown = estimateErrors.Where(e => e.At >= rateKnownFrom).ToList() is { Count: > 0 } late
                    ? $"{late.Min(e => e.Ms):+0.000;-0.000} to {late.Max(e => e.Ms):+0.000;-0.000} ms" : "none",
            };
        }
        finally
        {
            foreach (var m in machines)
            {
                m.Sender.OnSentSamples = null;
                m.Sender.PacketTapForTest = null;
                try { m.Sender.Dispose(); } catch { /* teardown */ }
                try { m.Receiver.Dispose(); } catch { /* teardown */ }
            }
            AudioClock.UseSimulated(null);
        }
    }
}
