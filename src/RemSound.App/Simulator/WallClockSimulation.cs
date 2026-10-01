using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using RemSound.Core;
using RemSound.Receiver;
using RemSound.Sender;

namespace RemSound.App.Simulator;

/// <summary>What a wall-clock run found: the sound as <see cref="SimAnalysis"/> hears it, and how the machine itself
/// behaved - how late the simulated cards' own threads were woken, the longest wait between packets at the receiver's
/// socket, and the garbage collections in the run.</summary>
internal sealed record WallClockResult(SimScenario Scenario, SimAnalysis.Findings Findings, long Underruns, long PacketsSent, long PacketsReceived,
    double WorstSendLateMs, double WorstOutputLateMs, int MaxArrivalGapMs, int Collections, double RealSeconds)
{
    /// <summary>How the sound reached the sender: "fed to the lane", "push mode" or "the mixing engine".</summary>
    public string CapturePath { get; init; } = "fed to the lane";

    /// <summary>Each second's longest wait between packets at the receiver's socket (ms), the first second included.</summary>
    public IReadOnlyList<int> ArrivalGapBySecond { get; init; } = [];

    /// <summary>The machine stalled one of the simulated cards' threads by more than a whole 10 ms block: what was heard
    /// says more about this machine at that moment than about RemSound.</summary>
    public bool MachineStalled => Math.Max(WorstSendLateMs, WorstOutputLateMs) > 10;
}

/// <summary>
/// THE WALL-CLOCK MODE (2026-09-30, the simulator review's seventh improvement). The simulator proper runs in virtual time
/// on one thread, which is what makes it exact and repeatable - and blind to everything Windows does to timing: the two
/// real findings of 28 September (the 1 ms timer ignored for a hidden process, MMCSS refusing a 33rd thread) were both of
/// that kind. Here the same real sender and receiver run in real time: the sender's socket sends to the receiver's own
/// listener over loopback, on the receiver's own listening thread; each simulated card is a thread of its own, paced by
/// the machine's clock at the card's period, as a driver's callback thread is; the audio clock is the real one. It is
/// slower (a second takes a second), it is not repeatable to the sample, and it depends on the machine - so it says how
/// the machine behaved beside what was heard, and a run the machine stalled is said to be so rather than blamed on
/// RemSound. The network is the machine's own loopback: none of the simulated network's faults are here.
/// </summary>
internal static class WallClockSimulation
{
    /// <param name="fastTimer">Hold Windows' fast timer for the run, as the app does from when sound starts moving until it
    /// stops (PerformanceMode.KeepFastTimerWhileStreaming). Without it this windowless process is held to the 15.6 ms
    /// timer, as a minimised RemSound was before 28 September.</param>
    public static WallClockResult Run(SimScenario s, bool fastTimer = true)
    {
        if (s.Network.Replay is not null || s.ContinuousAutoTune || s.WasapiOutputStage || s.Config == SimConfig.Both)
            throw new ArgumentException("the wall-clock mode runs one lane over loopback: no replay, auto-tune, output stage or both kinds of output");
        if (s.CaptureDevices is { Count: > 0 } && (s.Config != SimConfig.WasapiOnly || s.CaptureDevices.Any(d => d.Card.DriftPpm != 0 || d.Card.StallMs > 0 || d.Card.StallOnceMs > 0)))
            throw new ArgumentException("the wall-clock mode's capture devices feed the WASAPI lane, and neither drift nor stall");
        var wall = Stopwatch.StartNew();
        var asio = s.Config == SimConfig.AsioOnly;
        var sendCard = s.SendCard ?? Simulation.DefaultCard(asio);
        var receiveCard = s.ReceiveCard ?? Simulation.DefaultCard(asio);
        if (sendCard.StallMs > 0 || sendCard.StallOnceMs > 0 || receiveCard.StallMs > 0 || receiveCard.StallOnceMs > 0)
            throw new ArgumentException("the wall-clock mode's cards do not stall: what stalls them here is the machine itself");
        var port = FreeUdpPort();
        var receiverAt = new IPEndPoint(IPAddress.Loopback, port);

        AudioClock.UseSimulated(null);
        JamulusStyle.Reset();
        CustomOpusPeers.Clear();
        var diagnosticsWere = DiagnosticsGate.Enabled;
        DiagnosticsGate.Enabled = true;   // the arrival gaps are only timed with diagnostics on
        var (key, fingerprint) = RemSoundCrypto.ForPlainPassword("simulator");
        using var receiver = new AudioReceiver();
        using var sender = new AudioSender();
        using var timer = new SystemTimerResolution(1);
        using var timerFlag = fastTimer ? PerformanceMode.RealTimerFlagForTest() : null;
        if (fastTimer) PerformanceMode.KeepFastTimerWhileStreaming(true);
        var stop = new CancellationTokenSource();
        try
        {
            receiver.SetOutputDevices([]);
            receiver.AudioKey = key;
            receiver.AudioFingerprint = fingerprint;
            receiver.SetActiveOutputLanes(wasapiActive: !asio, asioActive: asio);
            receiver.SetIndependentLaneLatency(asio);
            var route = asio ? RenderRoute.AsioLane : RenderRoute.Mixed;
            var countersRoute = asio ? RenderRoute.AsioLane : RenderRoute.WasapiLane;
            if (asio)
            {
                receiver.SetMaxLatencyMsFor(RenderRoute.AsioLane, s.BufferMs);
                receiver.SetMaxLatencyMsFor(RenderRoute.WasapiLane, s.BufferMs);
            }
            else receiver.MaxLatencyMs = s.BufferMs;
            receiver.SetSmoothness(s.Smoothness);
            receiver.PerStreamBuffers = s.PerStreamBuffers;
            receiver.SetPlaybackEnabled(true);
            receiver.Start(port);

            sender.SetReceivers([receiverAt]);
            sender.AudioKey = key;
            sender.AudioFingerprint = fingerprint;
            Simulation.ConfigureCodec(sender, s.Codec);
            if (s.Codec is SimCodec.Custom64 or SimCodec.Custom128) CustomOpusPeers.Note(receiverAt.Address);
            if (asio) sender.UseAsioLaneForTest();
            sender.UseDefaultLaneForTest(asio ? RenderRoute.WasapiLane : RenderRoute.Mixed);
            // The capture side, when the scenario has devices: opened by the sender as it opens real ones. The mixing
            // engine runs on its OWN thread here, woken by Windows' own timer - the one part of the sending side that sleeps,
            // and so the part the 15.6 ms timer reaches (28 Sep).
            var inputs = new List<(SimCaptureDevice Device, SimCaptureInput Input)>();
            var capturePath = "fed to the lane";
            if (s.CaptureDevices is { Count: > 0 } devices)
            {
                var specs = new List<CaptureSourceSpec>();
                for (var i = 0; i < devices.Count; i++)
                {
                    var d = devices[i];
                    var id = d.PerApp ? ProcessLoopbackId.Format(4000 + i) : $"sim:{d.Name}";
                    var kind = d.PerApp ? CaptureKind.ProcessLoopback : d.Loopback ? CaptureKind.Loopback : CaptureKind.Input;
                    specs.Add(new CaptureSourceSpec(id, kind, d.Name));
                    inputs.Add((d, new SimCaptureInput(d, DeviceLatency.CaptureMs(d.Card.Frames * 1000.0 / d.SampleRate, CaptureSource.CaptureBufferMs))));
                }
                CaptureDevicesForTest.Open = spec => inputs[specs.FindIndex(x => x.DeviceId == spec.DeviceId)].Input;
                CaptureDevicesForTest.ManualMixTick = false;
                sender.Configure(specs);
                sender.Start();
                capturePath = sender.WasapiPushModeForTest ? "push mode" : sender.WasapiMixerForTest is not null ? "the mixing engine" : "none";
            }

            var start = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 10;   // every card starts a tenth of a second from now
            double Seconds(long ticks) => (ticks - start) / (double)Stopwatch.Frequency;

            // The cards' threads are at the highest ordinary priority and NOT in Windows' audio class ("Pro Audio"), which a
            // real driver's thread is. They keep time by spinning, and a thread of that class that never rests makes Windows
            // hold back the process's other audio-class threads: with the boost on these two, the receiver's own listening
            // thread (which is in that class, as in the app) was left waiting 10 to 20 ms for packets that had been sent 1 to
            // 3 ms apart - once a second, in every run. Without it the same packets reach it 1 to 2 ms apart. Measured
            // 2026-09-30; a real card's thread sleeps between callbacks, so it is this harness's doing and not the app's.
            // ---- the sending card: its own thread, a block every period ----
            var sendPeriod = sendCard.Frames / (Simulation.SampleRate * (1 + sendCard.DriftPpm * 1e-6));
            double worstSendLate = 0;
            var sendThread = new Thread(() =>
            {
                var dice = new Random(s.Seed);
                long captured = 0;
                for (var k = 1; k * sendPeriod <= s.Seconds && !stop.IsCancellationRequested; k++)
                {
                    var late = WaitUntil(start + (long)((k * sendPeriod + sendCard.PhaseMs / 1000 + dice.NextDouble() * sendCard.JitterMs / 1000) * Stopwatch.Frequency));
                    if (k > 10 && late > worstSendLate) worstSendLate = late;
                    var block = new float[sendCard.Frames * 2];
                    for (var i = 0; i < sendCard.Frames; i++)
                    {
                        var n = captured + i;
                        block[2 * i] = (float)(Simulation.ToneLevel * Math.Sin(2 * Math.PI * Simulation.ToneHz * n / Simulation.SampleRate));
                        block[2 * i + 1] = n % (long)(Simulation.ClickEverySec * Simulation.SampleRate) == 0 ? Simulation.ClickLevel : 0f;
                    }
                    captured += sendCard.Frames;
                    if (asio) sender.FeedAsioLaneForTest(block);
                    else sender.FeedDefaultLaneForTest(block);
                }
            }) { IsBackground = true, Name = "wall-clock sending card", Priority = ThreadPriority.Highest };
            // Or the capture devices: a thread each, a callback every period, bringing what the device captured up to then.
            var deviceLate = new double[inputs.Count];
            var deviceThreads = inputs.Select((x, index) => new Thread(() =>
            {
                var dice = new Random(s.Seed + 10 + index);
                var card = x.Device.Card;
                for (var k = 1; k * x.Device.Period <= s.Seconds && !stop.IsCancellationRequested; k++)
                {
                    var late = WaitUntil(start + (long)((k * x.Device.Period + card.PhaseMs / 1000 + dice.NextDouble() * card.JitterMs / 1000) * Stopwatch.Frequency));
                    if (k > 10 && late > deviceLate[index]) deviceLate[index] = late;
                    x.Input.Deliver(k);
                }
            }) { IsBackground = true, Name = $"wall-clock capture device {index + 1}", Priority = ThreadPriority.Highest }).ToList();

            // ---- the output card: its own thread, a read every period, heard one card buffer later ----
            var receivePeriod = receiveCard.Frames / (Simulation.SampleRate * (1 + receiveCard.DriftPpm * 1e-6));
            var output = new List<float>((int)(s.Seconds * Simulation.SampleRate * 2) + 4096);
            var times = new List<(int Frame, double Time)>();
            double worstOutputLate = 0;
            var provider = receiver.SimulationOutput(route);
            var outputThread = new Thread(() =>
            {
                var dice = new Random(s.Seed + 1);
                var readBytes = new byte[receiveCard.Frames * 8];
                for (var k = 1; k * receivePeriod <= s.Seconds && !stop.IsCancellationRequested; k++)
                {
                    var due = start + (long)((k * receivePeriod + receiveCard.PhaseMs / 1000 + dice.NextDouble() * receiveCard.JitterMs / 1000) * Stopwatch.Frequency);
                    var late = WaitUntil(due);
                    if (k > 10 && late > worstOutputLate) worstOutputLate = late;
                    Array.Clear(readBytes);
                    provider.Read(readBytes, 0, readBytes.Length);
                    times.Add((output.Count / 2, Seconds(Stopwatch.GetTimestamp()) + receivePeriod));
                    output.AddRange(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(readBytes).ToArray());
                }
            }) { IsBackground = true, Name = "wall-clock output card", Priority = ThreadPriority.Highest };

            var collectionsBefore = GC.CollectionCount(0);
            if (inputs.Count == 0) sendThread.Start();
            foreach (var t in deviceThreads) t.Start();
            outputThread.Start();
            // The main window's once-a-second work that the receiver needs: each stream's own buffer.
            var cardMs = (int)Math.Ceiling(receivePeriod * 1000);
            var maxGap = 0;
            var gaps = new List<int>();
            for (var sec = 1; sec <= (int)s.Seconds; sec++)
            {
                WaitUntil(start + sec * Stopwatch.Frequency, spin: false);
                receiver.UpdatePerStreamNeeds(_ => cardMs, _ => false);
                var diag = receiver.TakeDiagnosticsSnapshot();
                gaps.Add(diag.MaxArrivalGapMs);
                if (sec > 1 && diag.MaxArrivalGapMs > maxGap) maxGap = diag.MaxArrivalGapMs;
            }
            if (inputs.Count == 0) sendThread.Join(TimeSpan.FromSeconds(s.Seconds + 5));
            foreach (var t in deviceThreads) t.Join(TimeSpan.FromSeconds(s.Seconds + 5));
            outputThread.Join(TimeSpan.FromSeconds(s.Seconds + 5));
            if (deviceLate.Length > 0) worstSendLate = deviceLate.Max();

            var from = (int)(s.AnalyseFromSecond * Simulation.SampleRate);
            var listened = from <= 0 ? output : output.Skip(from * 2).ToList();
            var listenedTimes = from <= 0 ? times : times.Where(b => b.Frame >= from).Select(b => (b.Frame - from, b.Time)).ToList();
            var findings = SimAnalysis.Analyse(listened, listenedTimes, Simulation.SampleRate);
            return new WallClockResult(s, findings, receiver.TuneBlockingUnderrunsFor(countersRoute), sender.PacketsSent, receiver.PacketsReceived,
                worstSendLate, worstOutputLate, maxGap, GC.CollectionCount(0) - collectionsBefore, wall.Elapsed.TotalSeconds) { ArrivalGapBySecond = gaps, CapturePath = capturePath };
        }
        finally
        {
            stop.Cancel();
            try { sender.Stop(); } catch { /* teardown */ }
            try { receiver.Stop(); } catch { /* teardown */ }
            CaptureDevicesForTest.Open = null;
            CaptureDevicesForTest.ManualMixTick = false;
            DiagnosticsGate.Enabled = diagnosticsWere;
            JamulusStyle.Reset();
            CustomOpusPeers.Clear();
        }
    }

    /// <summary>Wait for a moment on the machine's clock. A card's thread spins all the way, as a driver's own thread is
    /// woken by its hardware on time: a sleep here is at the mercy of Windows' timer, which a windowless process gets at
    /// 15.6 ms whatever it asks for (28 Sep's finding - the first wall-clock run had its own 10 ms cards woken 13.6 ms
    /// late by it). The once-a-second housekeeping may sleep. Returns how late the wait ended, in ms.</summary>
    private static double WaitUntil(long dueTicks, bool spin = true)
    {
        while (true)
        {
            var left = dueTicks - Stopwatch.GetTimestamp();
            if (left <= 0) return -left * 1000.0 / Stopwatch.Frequency;
            if (!spin) Thread.Sleep(1);
            else if (left > Stopwatch.Frequency / 1000) Thread.Yield();   // more than a millisecond to go: let others in
            else Thread.SpinWait(40);
        }
    }

    /// <summary>A UDP port nothing is on, for the run's own listener - never one of RemSound's own.</summary>
    private static int FreeUdpPort()
    {
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }

    public static string Describe(WallClockResult r)
    {
        var f = r.Findings;
        var s = r.Scenario;
        return $"{s.Name}: {s.Config}, {s.Codec}, buffer {s.BufferMs} ms, {(s.CaptureDevices is null ? "" : r.CapturePath + ", ")}on the wall clock over loopback -> "
             + (f.ClicksHeard == 0 ? "NOTHING HEARD" :
                $"delay {f.DelayMedianMs:0.0} ms (min {f.DelayMinMs:0.0}, max {f.DelayMaxMs:0.0}); "
              + $"{(f.Clean ? "CLEAN" : $"{f.Glitches} glitches ({f.QuietMs} quiet ms, {f.StepClicks} clicks, first at {f.FirstGlitchAt})")}; "
              + $"pitch within {f.WorstPitchCents:0.0} cents")
             + $"; underruns {r.Underruns}, packets {r.PacketsSent} sent {r.PacketsReceived} received; the machine: cards woken up to "
             + $"{r.WorstSendLateMs:0.00} ms (sending) and {r.WorstOutputLateMs:0.00} ms (output) late, {r.MaxArrivalGapMs} ms the longest wait "
             + $"between packets (by second: {string.Join(" ", r.ArrivalGapBySecond)}), {r.Collections} collections{(r.MachineStalled ? " - THE MACHINE STALLED A CARD" : "")}; {r.RealSeconds:0.0} s";
    }
}
