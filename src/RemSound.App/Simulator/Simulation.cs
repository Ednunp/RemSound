using System.Diagnostics;
using System.Net;
using NAudio.Wave;
using RemSound.Core;
using RemSound.Receiver;
using RemSound.Sender;

namespace RemSound.App.Simulator;

/// <summary>Which output devices the receiving end has ticked - RemSound's three audio configurations.</summary>
internal enum SimConfig { WasapiOnly, AsioOnly, Both }

/// <summary>What the sending end puts on the wire from the channels under test. PCM is what the app sends when PCM is
/// picked: one packet per capture callback on every lane (tight latency, always on in the app and the service), cut at the
/// standard frame size. The simulator's older PCM - 5 ms frames, tight latency off - is not something the app has done since
/// May, and the latency lab's separate "ASIO as PCM per callback" went with that switch (2026-09-28), so both are this one.</summary>
internal enum SimCodec { Pcm, Opus2_5, Opus10, Opus20, Custom128, Custom64 }

/// <summary>A fake sound card: its buffer size in frames, how late its callbacks can be, how far its clock is off
/// (parts per million, + is fast), and an optional stall (every N seconds it stops for M ms, then catches up). A ONE-OFF
/// stall (StallAtSec, StallOnceMs) is a device that stops and comes back: the callbacks it missed are gone, not caught
/// up - a long stall is not a late driver thread. PhaseMs (2026-09-28): the card's callbacks start this much later than
/// the others', so it sits at a chosen phase against the WASAPI output stage's 10 ms producer (which, like every card,
/// otherwise ticks on the same whole periods).</summary>
internal sealed record SimCard(int Frames, double JitterMs = 0, double DriftPpm = 0, double StallEverySec = 0, double StallMs = 0,
    double StallAtSec = 0, double StallOnceMs = 0, double PhaseMs = 0);

/// <summary>A fake network and the receiving machine's handling of it: one-way delay, random extra delay (mean), lost and
/// doubled packets, delivery in lumps (a packet waits for the next multiple of LumpMs, as wifi and a busy receiver do),
/// stalls, and the receiving PC's own hiccups - the receive thread woken late by Windows (HiccupPercent of packets,
/// up to HiccupMs late), which even two fast desktops on a wired network have. A ONE-OFF stall (StallAtSec,
/// StallOnceMs) holds everything sent in it and delivers it at the end.
///
/// <para>Networks as they really misbehave (2026-09-30, the simulator review's fourth improvement). LOSS IN BURSTS: real
/// loss comes in runs, not one packet at a time - with BurstLossPercent chance a packet, a burst starts, and every packet
/// is lost until it ends (BurstLossLength packets on average). A QUEUE THAT BUILDS AND DRAINS: every QueueEverySec
/// seconds on average a queue somewhere on the path fills - the delay climbs to QueuePeakMs over QueueRiseMs and falls
/// back over QueueFallMs - rather than each packet's delay being its own dice. Both are off at zero and draw no dice
/// unless used, so every run without them is sample for sample what it was.</para></summary>
internal sealed record SimNetwork(string Name, double BaseMs, double JitterMs = 0, double LossPercent = 0, double DuplicatePercent = 0,
    double LumpMs = 0, double StallEverySec = 0, double StallMs = 0, double ReorderPercent = 0, double HiccupPercent = 0, double HiccupMs = 0,
    double StallAtSec = 0, double StallOnceMs = 0, double BurstLossPercent = 0, double BurstLossLength = 4, double QueuePeakMs = 0,
    double QueueEverySec = 5, double QueueRiseMs = 200, double QueueFallMs = 600)
{
    /// <summary>The internet's half a percent of loss, in bursts of four packets on average instead of one at a time.</summary>
    public static readonly SimNetwork BurstyInternet = new("internet, loss in bursts", 12, 3, ReorderPercent: 0.05, HiccupPercent: 0.2, HiccupMs: 5,
        BurstLossPercent: 0.125, BurstLossLength: 4);
    /// <summary>A path whose queue fills every few seconds: the delay climbs 30 ms over a fifth of a second and drains
    /// over the next six tenths.</summary>
    public static readonly SimNetwork Congested = new("internet, a queue that builds and drains", 12, 2, HiccupPercent: 0.2, HiccupMs: 5,
        QueuePeakMs: 30, QueueEverySec: 4);

    /// <summary>A recorded network played back in place of all the above but <see cref="BaseMs"/>, the one-way delay
    /// (2026-09-30): see <see cref="NetworkReplay"/>.</summary>
    public NetworkReplay? Replay { get; init; }

    /// <summary>A recording replayed, with the one-way delay the network is taken to have.</summary>
    public static SimNetwork Replaying(NetworkReplay replay, double oneWayMs) => new(replay.Name, oneWayMs) { Replay = replay };

    /// <summary>Two good desktops on a wired network: tiny jitter, and Windows waking the receive thread a few ms late
    /// about once in a thousand packets.</summary>
    public static readonly SimNetwork Wired = new("wired LAN, two desktops", 0.2, 0.1, HiccupPercent: 0.1, HiccupMs: 3);
    /// <summary>The perfect case: no jitter, no hiccups - what the maths alone allows.</summary>
    public static readonly SimNetwork Perfect = new("perfect (no jitter at all)", 0.2);
    public static readonly SimNetwork Wifi = new("home wifi", 2, 1.5, LossPercent: 0.2, LumpMs: 6, HiccupPercent: 0.2, HiccupMs: 5);
    public static readonly SimNetwork Internet = new("internet", 12, 3, LossPercent: 0.5, ReorderPercent: 0.05, HiccupPercent: 0.2, HiccupMs: 5);
    public static readonly SimNetwork Rough = new("rough internet", 25, 8, LossPercent: 2, DuplicatePercent: 0.5, ReorderPercent: 0.5, HiccupPercent: 0.5, HiccupMs: 8);
    public static readonly SimNetwork Lumpy = new("lumpy receiver (Andre)", 0.5, 0.2, LumpMs: 24, StallEverySec: 4, StallMs: 60);
    /// <summary>Ed's laptop as the live test measured it (2026-09-26): wired, but packets reach its socket in lumps -
    /// typically 10 ms apart, 17 at the 90th percentile, up to about 40.</summary>
    public static readonly SimNetwork LaptopLumps = new("Ed's laptop (packets arrive in lumps)", 0.3, 0.2, LumpMs: 10, ReorderPercent: 0.1,
        HiccupPercent: 0.1, HiccupMs: 20, StallEverySec: 45, StallMs: 30);
}

/// <summary>One run: who sends what over which network into which configuration, at what buffer.</summary>
/// <param name="ToneOnWasapiLane">With both kinds of output: the tone goes out on the sender's WASAPI lane and the ASIO lane
/// carries silence (normally the other way round). Every output plays every stream, so the two lanes cannot both carry the
/// same tone - each output would hear two copies of it, a few ms apart, and comb-filter them.</param>
/// <param name="WasapiLaneSendCard">With both kinds of output: the sender's WASAPI capture (10 ms blocks unless given).</param>
/// <param name="WasapiLaneReceiveCard">With both kinds of output: the receiver's WASAPI card (10 ms unless given).</param>
/// <param name="WasapiOutputStage">Put the WASAPI output stage between the engine and every WASAPI card - the device
/// buffer, its cushion and its rate trimmer, fed by the 10 ms producer - as the app has it. Off, the card reads the
/// engine directly, as the simulator always did.</param>
/// <param name="ContinuousAutoTune">Run the main window's continuous auto-tune on every lane with audio (see
/// <see cref="SimMainWindow"/>), every <paramref name="AutoTuneIntervalSec"/>.</param>
/// <param name="ChunkSeconds">Listen a stretch at a time and keep no audio - for runs of hours.</param>
/// <param name="DelayClicks">The right channel's clicks, which time the delay. Off, the right channel is silent.</param>
/// <param name="PlantClickAtSecond">A click planted in the LEFT channel's tone at the sender, at this second.</param>
/// <param name="Diagnostics">The app's diagnostics switch (logs or an auto-tune on): the probes and the per-second
/// readings behind the latency readout. On whenever the auto-tune runs, as in the app.</param>
/// <param name="PushToTalk">Push to talk at the sender, and what the run does with it (<see cref="SimPushToTalk"/>).</param>
/// <param name="SendWasapiOnly">The sender sends as the send-only service does (2026-09-28): WASAPI only - one untagged
/// stream from 10 ms capture blocks - whatever the receiver's configuration. So a service feeding a listener on ASIO (Ed's
/// laptop) can be run: Config says what the receiver plays on.</param>
/// <param name="ReceiverActions">Anything else done to the receiver at a simulated moment - a slider moved on the other
/// lane, say.</param>
/// <param name="Music">The left channel carries the music-like signal (<see cref="SimMusic"/>) in place of the steady tone,
/// and the result's <see cref="SimResult.Music"/> says what was heard in it: each fault, where in the music it fell and
/// how audible it is. The tone's own findings (glitches, pitch, noise) mean nothing in such a run; its delay figures do.</param>
/// <param name="EverySecondDo">Called once a simulated second with the simulated time, the receiver and that second's
/// per-stream reports (the ones the per-stream buffers were sized from - asked once, as the app asks), after the main
/// window's work: for a run that watches the receiver as some other part of the app does (the log, SimLogs).</param>
internal sealed record SimScenario(
    string Name,
    SimConfig Config,
    SimCodec Codec,
    SimNetwork Network,
    int BufferMs,
    double Seconds = 10,
    SimCard? SendCard = null,
    SimCard? ReceiveCard = null,
    bool PerStreamBuffers = true,
    int WasapiLaneBufferMs = 0,
    int Seed = 1,
    bool KeepAudio = false,
    bool ReorderWait = true,
    bool LowestDelayTune = false,
    double AnalyseFromSecond = 0,
    int SoftTargetMs = 0,
    double SoftTargetAtSecond = 0,
    int Smoothness = 3,
    int HandTargetMs = 0,
    double HandTargetAtSecond = 0,
    bool ToneOnWasapiLane = false,
    SimCard? WasapiLaneSendCard = null,
    SimCard? WasapiLaneReceiveCard = null,
    bool WasapiOutputStage = false,
    bool ContinuousAutoTune = false,
    int AutoTuneIntervalSec = 5,
    double ChunkSeconds = 0,
    bool DelayClicks = true,
    double PlantClickAtSecond = 0,
    float PlantClickLevel = 0.5f,
    bool Diagnostics = false,
    SimPushToTalk? PushToTalk = null,
    IReadOnlyList<(double At, Action<AudioReceiver> Do)>? ReceiverActions = null,
    bool SendWasapiOnly = false,
    bool RecordNetwork = false,
    IReadOnlyList<SimCaptureDevice>? CaptureDevices = null,
    double MixTickJitterMs = 1.5,
    bool Music = false,
    Action<double, AudioReceiver, IReadOnlyList<AudioReceiver.StreamNeedReport>>? EverySecondDo = null);

/// <summary>
/// Push to talk in a run (2026-09-28): the profile's settings at the sender, and the key pressed and released at simulated
/// moments - through the real key logic (PushToTalkKeyState) into the real gate on the send lanes. Optionally the tone's
/// level changing over the run (for sound-level mode), a second tone on the lane the first isn't on (in "both": 1.6 kHz, so
/// each lane can be heard apart), and a DAW track on the plugin lane (440 Hz). The sender's recording tap is always kept.
/// </summary>
internal sealed record SimPushToTalk(
    PushToTalkSettings Settings,
    IReadOnlyList<(double At, bool Down)> Key,
    IReadOnlyList<(double From, float Level)>? ToneLevels = null,
    bool OtherLaneTone = false,
    bool PluginTrack = false)
{
    public const double OtherLaneHz = 1600;
    public const float OtherLaneLevel = 0.3f;
    public const double PluginHz = 440;
    public const float PluginLevel = 0.3f;
}

/// <summary>What came out of one run.</summary>
internal sealed record SimResult(SimScenario Scenario, SimAnalysis.Findings Findings, long Underruns, long PacketsSent, long PacketsLost,
    long FecRecoveries, long UnrecoveredGaps, long CustomConcealed, float[]? Audio, double RealSeconds, int GapUsedMs, int CardPeriodMs)
{
    /// <summary>The frame, in whole ms rounded up, of the stream under test - what the auto-tune's codec floor uses.</summary>
    public int FrameMs => Scenario.Codec switch
    {
        SimCodec.Pcm => 5, // the app announces its standard PCM frame (233 samples), whatever the callback
        SimCodec.Opus2_5 => 3,
        SimCodec.Opus10 => 10,
        SimCodec.Opus20 => 20,
        SimCodec.Custom128 => 3,
        SimCodec.Custom64 => 2,
        _ => 3,
    };

    /// <summary>Late or doubled packets the receiver threw away.</summary>
    public long LateDropped { get; init; }

    /// <summary>With the music-like signal: every fault heard in it, placed and weighed (null in a run on the tone).</summary>
    public SimMusic.Findings? Music { get; init; }

    /// <summary>With simulated capture devices: which way the sender took them ("push mode" or "the mixing engine"), and each
    /// source's drift corrector at the end (the mixing engine only).</summary>
    public string CapturePath { get; init; } = "fed to the lane";
    public IReadOnlyList<(string Name, double Ratio, bool Tracking)> CaptureDrift { get; init; } = [];

    /// <summary>With RecordNetwork: the receiver's own recording of what arrived (its last minute), as the app saves one.</summary>
    public NetworkRecording? Recording { get; init; }
    /// <summary>Every stream the receiver heard packets on, by its session name (which carries its lane - "Mixed" for an
    /// untagged stream such as the service's).</summary>
    public IReadOnlyList<string> Streams { get; init; } = [];

    /// <summary>PCM frames the receiver made up for lost packets.</summary>
    public long PcmConcealed { get; init; }

    /// <summary>Times the receiver's playback waited to refill after a real stop (over 100 ms of nothing): a momentary
    /// empty never counts here.</summary>
    public long LongGapRearms { get; init; }

    /// <summary>With the "keep dropouts rare" tuner running: where it finished, and its moves ("24>22@15s ...").</summary>
    public int TunedMs { get; init; }
    public string TunerMoves { get; init; } = "";

    /// <summary>The reorder wait's holds, and how many the missing packet filled.</summary>
    public long ReorderWaits { get; init; }
    public long ReorderFilled { get; init; }

    /// <summary>With both kinds of output: what the WASAPI output played, listened to as the ASIO one is (null otherwise),
    /// and its underruns.</summary>
    public SimAnalysis.Findings? WasapiOutput { get; init; }
    public long WasapiOutputUnderruns { get; init; }

    /// <summary>The click-trim safety net: how often it cut the buffer, and how much it cut in all (ms).</summary>
    /// <summary>Snaps down from a lowered target (SessionPlayout's DescentDepthBias block), and what they skipped (ms).</summary>
    public long SnapCount { get; init; }
    public double SnapSkippedMs { get; init; }
    public long TrimFires { get; init; }
    public double TrimDroppedMs { get; init; }
    /// <summary>Each second: the listened output's drift loop's clock-ratio estimate (its first stream's), in ppm.</summary>
    public IReadOnlyList<double> DriftPpmBySecond { get; init; } = [];
    /// <summary>Each second: the click-trim's firings so far (a lifetime total).</summary>
    public IReadOnlyList<long> TrimsBySecond { get; init; } = [];

    /// <summary>Stream sessions the receiver opened in the whole run (one per stream, unless something re-opened one).</summary>
    public long SessionsOpened { get; init; }

    /// <summary>The main window's work in the loop (the continuous auto-tune and the latency readout), when it ran.</summary>
    public SimMainWindow? Window { get; init; }

    /// <summary>Each second: the listened output's tune-blocking underruns so far (a lifetime total).</summary>
    public IReadOnlyList<long> UnderrunsBySecond { get; init; } = [];

    /// <summary>Listened a stretch at a time: each stretch's start (s) and findings, for the output under test and, with
    /// both kinds of output, the WASAPI one.</summary>
    public IReadOnlyList<(double FromSec, SimAnalysis.Findings Findings)> Stretches { get; init; } = [];
    public IReadOnlyList<(double FromSec, SimAnalysis.Findings Findings)> WasapiStretches { get; init; } = [];

    /// <summary>With diagnostics on: the receiver's post-decode step probe each second (cross-buffer, within-buffer).</summary>
    public IReadOnlyList<(float Cross, float Within)> PostDecodeSteps { get; init; } = [];

    /// <summary>With the WASAPI output stage: its reading each second (device buffer, cushion target, trim).</summary>
    public IReadOnlyList<WasapiOutputStage> StageBySecond { get; init; } = [];

    /// <summary>Each second: every copy of every stream (the lane it was sent on, the output it plays on, mirror or not,
    /// its mean depth in ms, its drift loop's clock ratio and whether it has a reading yet) - see AudioReceiver.ReplicasForTest.</summary>
    public IReadOnlyList<List<(RenderRoute Announced, RenderRoute Plays, bool Mirror, double DepthMs, double Ratio, bool Tracking)>> ReplicasBySecond { get; init; } = [];
    /// <summary>With the WASAPI output stage: the card pulls it could not fill, in the whole run, and so far each second.</summary>
    public long StageShortReads { get; init; }
    public IReadOnlyList<long> StageShortReadsBySecond { get; init; } = [];
    /// <summary>With the WASAPI output stage: its time-averaged level over each second (ms) - integrated at every feed and pull.</summary>
    public IReadOnlyList<double> StageLevelBySecond { get; init; } = [];

    /// <summary>With the audio kept: when each block of the listened output was heard (its first frame, in simulated
    /// seconds), and the same for the WASAPI output of "both".</summary>
    public IReadOnlyList<(int Frame, double Time)> AudioTimes { get; init; } = [];
    public float[]? WasapiAudio { get; init; }
    public IReadOnlyList<(int Frame, double Time)> WasapiAudioTimes { get; init; } = [];

    /// <summary>With push to talk: what the sender's recording tap was handed - each block's time, lane and loudest
    /// sample.</summary>
    public IReadOnlyList<(double Time, RenderRoute Lane, float Peak)> Recorded { get; init; } = [];

    /// <summary>What the auto-tune's formula would ask for on this stream's own arrivals (lean = the lab's lean margins).</summary>
    public int TunerWouldAskMs(bool lean) => AudioReceiver.PerStreamNeedMs(GapUsedMs, CardPeriodMs, FrameMs, lean);
}

/// <summary>
/// THE SIMULATOR (Ed, 2026-09-26: "I actually think this simulator is something we need to build anyway"). The REAL
/// AudioSender and AudioReceiver, with fake sound cards at each end and a fake network between, all on one thread in
/// virtual time: every callback, packet and read is an event at a simulated moment, and the audio path's clock
/// (<see cref="AudioClock"/>) reads that moment. So a run is exact, the same every time, and far faster than real time.
///
/// <para>The signal: LEFT a steady 1 kHz tone, RIGHT a click every 500 ms. <see cref="SimAnalysis"/> measures the delay
/// from the clicks and every break-up, click, pitch wobble and noise from the tone.</para>
///
/// <para>What is real: the send lanes (packetising, Opus/PCM/custom encode, encryption), the whole receiver (decrypt,
/// decode, jitter buffer, trim glide, drift correction, concealment, the lanes and mirrors of the three configurations,
/// the latency lab's per-stream sizing), and on request the WASAPI output stage (device buffer, cushion, rate trimmer) and
/// the main window's continuous auto-tune and latency readout. What is simulated: the sound cards, the network, and the
/// WASAPI capture engine's timing (its 10 ms blocks are fed in; the engine itself is device code).</para>
/// </summary>
internal static class Simulation
{
    public const int SampleRate = 48000;
    public const double ToneHz = 1000;
    public const float ToneLevel = 0.5f;
    public const double ClickEverySec = 0.5;
    public const float ClickLevel = 0.9f;

    private static readonly IPEndPoint SenderAt = new(IPAddress.Parse("10.77.0.1"), 50123);
    private static readonly IPEndPoint ReceiverAt = new(IPAddress.Parse("10.77.0.2"), 47830);

    /// <summary>The default card for a configuration's side: ASIO at 32 frames (Ed's Audient), WASAPI at 10 ms.</summary>
    public static SimCard DefaultCard(bool asio) => asio ? new SimCard(32, JitterMs: 0.05) : new SimCard(480, JitterMs: 1);

    /// <summary>The tone and clicks, generated on one sending card's own clock.</summary>
    private sealed class Signal(double rate, SimScenario s)
    {
        private long captured;
        private readonly long plantAt = s.PlantClickAtSecond > 0 ? (long)(s.PlantClickAtSecond * SampleRate) : -1;
        private readonly IReadOnlyList<(double From, float Level)>? levels = s.PushToTalk?.ToneLevels;
        public double Rate { get; } = rate;

        /// <summary>The tone's level at a captured frame: the scenario's steps, or the usual level.</summary>
        private float LevelAt(long n)
        {
            if (levels is null) return ToneLevel;
            var level = ToneLevel;
            var t = n / (double)SampleRate;
            foreach (var (from, l) in levels) if (t >= from) level = l;
            return level;
        }

        public float[] Block(int frames)
        {
            var block = new float[frames * 2];
            var level = LevelAt(captured);
            for (var i = 0; i < frames; i++)
            {
                var n = captured + i;
                block[2 * i] = s.Music ? SimMusic.Sample(n / (double)SampleRate) : (float)(level * Math.Sin(2 * Math.PI * ToneHz * n / SampleRate));
                if (n == plantAt) block[2 * i] += s.PlantClickLevel;
                block[2 * i + 1] = s.DelayClicks && n % (long)(ClickEverySec * SampleRate) == 0 ? ClickLevel : 0f;
            }
            captured += frames;
            return block;
        }
    }

    /// <summary>One output card and everything heard from it: the audio (or, listened a stretch at a time, the part not
    /// yet listened to), and when each block of it is heard.</summary>
    /// <summary>A simulated card's played position (seconds of audio it has played), as a real card's clock reports it:
    /// each block it took plays from a given moment for its own length.</summary>
    private sealed class PlayedClock
    {
        private readonly Queue<(double Start, double Seconds)> playing = new();
        private double done;

        public void Taken(double start, double seconds) => playing.Enqueue((start, seconds));

        public double At(double t)
        {
            while (playing.Count > 0 && playing.Peek().Start + playing.Peek().Seconds <= t) done += playing.Dequeue().Seconds;
            return playing.Count > 0 && t > playing.Peek().Start ? done + (t - playing.Peek().Start) : done;
        }
    }

    private sealed class Listener(double sendRate, double receiveRate, double chunkSeconds)
    {
        public readonly List<float> Output = new();
        public readonly List<(int Frame, double Time)> Times = new();
        public readonly List<(double FromSec, SimAnalysis.Findings Findings)> Stretches = new();
        private long baseFrame;              // the frame Output[0] is, counted from the start of the run
        private long stretchStart;
        private long FramesSoFar => baseFrame + Output.Count / 2;
        private int StretchFrames => (int)(chunkSeconds * SampleRate);

        public void Heard(ReadOnlySpan<float> floats, double heardAt)
        {
            Times.Add(((int)(FramesSoFar - baseFrame), heardAt));
            foreach (var f in floats) Output.Add(f);
            // A stretch is listened to once a fifth of a second more has come after it, so its end is not the file's end.
            while (chunkSeconds > 0 && FramesSoFar >= stretchStart + StretchFrames + SampleRate / 5) ListenToStretch(stretchStart + StretchFrames);
        }

        private SimAnalysis.Findings Analyse(long from, long to) =>
            SimAnalysis.Analyse(Output, Times, sendRate, sendRate / receiveRate,
                countFromFrame: (int)(from - baseFrame), countToFrame: to == long.MaxValue ? int.MaxValue : (int)(to - baseFrame));

        private void ListenToStretch(long end)
        {
            Stretches.Add((stretchStart / (double)SampleRate, Analyse(stretchStart, end)));
            stretchStart = end;
            // Keep one second before the next stretch, so it starts with its clicks found; let the rest go.
            var keepFrom = Math.Max(baseFrame, end - SampleRate);
            var drop = (int)(keepFrom - baseFrame);
            if (drop <= 0) return;
            Output.RemoveRange(0, drop * 2);
            baseFrame = keepFrom;
            var firstKept = Times.FindLastIndex(t => t.Frame <= drop);
            if (firstKept > 0) Times.RemoveRange(0, firstKept);
            for (var i = 0; i < Times.Count; i++) Times[i] = (Times[i].Frame - drop, Times[i].Time);
        }

        /// <summary>All of it, or from a second on, or - listened a stretch at a time - the last stretch and the lot
        /// summed up.</summary>
        public SimAnalysis.Findings Finish(double fromSecond)
        {
            if (chunkSeconds > 0)
            {
                if (FramesSoFar > stretchStart + SampleRate) Stretches.Add((stretchStart / (double)SampleRate, Analyse(stretchStart, long.MaxValue)));
                return Summed(Stretches.Select(c => c.Findings).ToList());
            }
            var fromFrame = (int)(fromSecond * SampleRate);
            var listened = fromFrame <= 0 ? Output : Output.Skip(fromFrame * 2).ToList();
            var times = fromFrame <= 0 ? Times : Times.Where(b => b.Frame >= fromFrame).Select(b => (b.Frame - fromFrame, b.Time)).ToList();
            return SimAnalysis.Analyse(listened, times, sendRate, sendRate / receiveRate);
        }

        /// <summary>A long run's stretches summed up: counts added, the delay's range across them, the worst pitch and noise,
        /// and the typical figures as the median of the stretches'.</summary>
        private static SimAnalysis.Findings Summed(List<SimAnalysis.Findings> s)
        {
            if (s.Count == 0) return SimAnalysis.Analyse([], [], SampleRate);
            double Median(IEnumerable<double> v) { var o = v.Where(x => !double.IsNaN(x)).OrderBy(x => x).ToList(); return o.Count == 0 ? double.NaN : o[o.Count / 2]; }
            double Max(IEnumerable<double> v) { var o = v.Where(x => !double.IsNaN(x)).ToList(); return o.Count == 0 ? double.NaN : o.Max(); }
            double Min(IEnumerable<double> v) { var o = v.Where(x => !double.IsNaN(x)).ToList(); return o.Count == 0 ? double.NaN : o.Min(); }
            return new SimAnalysis.Findings(s.Sum(x => x.ClicksHeard), Median(s.Select(x => x.DelayMedianMs)), Min(s.Select(x => x.DelayMinMs)),
                Max(s.Select(x => x.DelayMaxMs)), s.Select(x => x.DelayFirstMs).FirstOrDefault(x => !double.IsNaN(x), double.NaN),
                s.Select(x => x.DelayLastMs).LastOrDefault(x => !double.IsNaN(x), double.NaN),
                s.Sum(x => x.Glitches), s.Sum(x => x.QuietMs), s.Sum(x => x.StepClicks), Max(s.Select(x => x.WorstPitchCents)),
                Median(s.Select(x => x.MedianSnrDb)), Min(s.Select(x => x.WorstSnrDb)), s.Sum(x => x.AnalysedSeconds),
                s.Select(x => x.FirstGlitchAt).FirstOrDefault(x => x is not null), Max(s.Select(x => x.PitchP95Cents)), Median(s.Select(x => x.PitchMedianCents)));
        }
    }

    /// <summary>
    /// Several runs at once, a thread each - for runs of simulated hours, so the gate is not kept waiting for them one after
    /// another. Each run's virtual clock is its own (<see cref="AudioClock"/> is per thread) and everything else in a run is
    /// its own objects; what is shared is a handful of machine-wide switches (the latency lab's, the reorder wait's test
    /// switch, the diagnostics gate), so only runs that leave every one of those as the app has them by default may run
    /// together - anything else is refused rather than left to trample the other run.
    /// </summary>
    public static SimResult[] RunTogether(params SimScenario[] scenarios)
    {
        foreach (var s in scenarios)
        {
            if (!s.ReorderWait || s.Diagnostics || s.ContinuousAutoTune || s.Codec is SimCodec.Custom64 or SimCodec.Custom128 || s.CaptureDevices is not null)
                throw new ArgumentException($"\"{s.Name}\" sets a machine-wide switch, so it cannot run beside another simulation");
        }
        var results = new SimResult[scenarios.Length];
        var failures = new Exception?[scenarios.Length];
        var diagnosticsWere = DiagnosticsGate.Enabled;
        DiagnosticsGate.Enabled = false; // as each run sets it, so none of them puts back a value another is running without
        var threads = scenarios.Select((s, i) => new Thread(() =>
        {
            try { results[i] = Run(s); }
            catch (Exception ex) { failures[i] = ex; }
        }) { IsBackground = true, Name = $"simulation {i}" }).ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());
        DiagnosticsGate.Enabled = diagnosticsWere;
        if (failures.FirstOrDefault(f => f is not null) is { } failed) throw new InvalidOperationException("a simulation failed: " + failed.Message, failed);
        return results;
    }

    public static SimResult Run(SimScenario s)
    {
        var wall = Stopwatch.StartNew();
        var rng = new Random(s.Seed);
        var asioSide = s.Config != SimConfig.WasapiOnly;
        // The sending end is the same kind as the receiving one, unless the run sends as the service does: WASAPI only,
        // one untagged stream from 10 ms capture blocks, whatever the receiver plays it on.
        var sendAsio = asioSide && !s.SendWasapiOnly;
        var sendCard = s.SendCard ?? DefaultCard(sendAsio);
        var receiveCard = s.ReceiveCard ?? DefaultCard(asioSide);
        var wasapiSendCard = s.WasapiLaneSendCard ?? new SimCard(480, JitterMs: 1);
        var wasapiReceiveCard = s.WasapiLaneReceiveCard ?? new SimCard(480, JitterMs: 1);
        var both = s.Config == SimConfig.Both;
        var sendBoth = both && !s.SendWasapiOnly;

        // Virtual time, in seconds, read by the audio path through AudioClock.
        double now = 0;
        var baseTicks = Stopwatch.GetTimestamp();
        AudioClock.UseSimulated(() => baseTicks + (long)(now * Stopwatch.Frequency));
        var queue = new PriorityQueue<Action, (double, long)>();
        long order = 0;
        void At(double t, Action a) => queue.Enqueue(a, (t, order++));

        JamulusStyle.Reset();
        CustomOpusPeers.Clear();
        var diagnosticsWere = DiagnosticsGate.Enabled;
        // The app switches its diagnostics on whenever an auto-tune is on (UpdateDiagnosticsGate); off otherwise, as a
        // user with logs off has it.
        DiagnosticsGate.Enabled = s.Diagnostics || s.ContinuousAutoTune;
        var (key, fingerprint) = RemSoundCrypto.ForPlainPassword("simulator");
        using var receiver = new AudioReceiver();
        using var sender = new AudioSender();
        long lost = 0;
        try
        {
            // ---- the receiving end: no devices, the configuration's lanes, the buffer under test ----
            receiver.SetOutputDevices([]);
            receiver.AudioKey = key;
            receiver.AudioFingerprint = fingerprint;
            receiver.SetActiveOutputLanes(wasapiActive: s.Config != SimConfig.AsioOnly, asioActive: s.Config != SimConfig.WasapiOnly);
            receiver.SetIndependentLaneLatency(s.Config != SimConfig.WasapiOnly);
            var route = s.Config switch { SimConfig.WasapiOnly => RenderRoute.Mixed, _ => RenderRoute.AsioLane };
            // The lane the listened output's streams are on: in WASAPI only the engine plays them all on the Mixed route,
            // but each stream sits on the one active lane, the WASAPI one - and that is where its counters are.
            var countersRoute = s.Config == SimConfig.WasapiOnly ? RenderRoute.WasapiLane : route;
            var wasapiLaneMs = s.WasapiLaneBufferMs > 0 ? s.WasapiLaneBufferMs : s.BufferMs;
            if (s.Config == SimConfig.WasapiOnly) receiver.MaxLatencyMs = s.BufferMs;
            else
            {
                receiver.SetMaxLatencyMsFor(RenderRoute.AsioLane, s.BufferMs);
                receiver.SetMaxLatencyMsFor(RenderRoute.WasapiLane, wasapiLaneMs);
            }
            receiver.SetSmoothness(s.Smoothness);
            receiver.SetPlaybackEnabled(true);
            receiver.PerStreamBuffers = s.PerStreamBuffers;
            receiver.NetworkRecorder.Enabled = s.RecordNetwork;
            receiver.LeanTuning = s.LowestDelayTune;
            StreamSession.ReorderWaitOffForTest = !s.ReorderWait;
            SessionPlayout.ConcealSeedForTest = s.Seed; // the underrun's noise burst, the same every run

            // ---- the sending end: the channels under test go out on the ASIO lane, or the WASAPI lane in WASAPI-only ----
            sender.SetReceivers([ReceiverAt]);
            sender.AudioKey = key;
            sender.AudioFingerprint = fingerprint;
            ConfigureCodec(sender, s.Codec);
            if (sendAsio) sender.UseAsioLaneForTest();
            sender.UseDefaultLaneForTest(sendAsio ? RenderRoute.WasapiLane : RenderRoute.Mixed);
            // What each capture card says its latency is, as its driver or device would - each lane its own (N7): the
            // ASIO lane its buffer; the WASAPI lane its device's engine period by DeviceLatency's rule.
            var wasapiCaptureCard = sendAsio ? wasapiSendCard : sendCard;
            sender.WasapiCaptureLatencyMsForTest = DeviceLatency.CaptureMs(wasapiCaptureCard.Frames * 1000.0 / SampleRate, CaptureSource.CaptureBufferMs);
            sender.AsioCaptureLatencyMsForTest = sendAsio ? sendCard.Frames * 1000.0 / SampleRate : 0;
            double lastArrival = 0;
            var inLossBurst = false;
            // A queue that builds and drains: the next episode's start, drawn when the last one is over (seconds); the
            // delay it adds to a packet sent now.
            double queueFrom = -1, queueDone = 0;
            double QueueDelay()
            {
                var n = s.Network;
                if (n.QueuePeakMs <= 0) return 0;
                double rise = n.QueueRiseMs / 1000, fall = n.QueueFallMs / 1000;
                if (queueFrom < 0 || now >= queueFrom + rise + fall)
                {
                    if (queueFrom >= 0) queueDone = queueFrom + rise + fall;
                    if (queueFrom < 0 || now >= queueDone)
                        queueFrom = Math.Max(now, queueDone) + -Math.Log(1 - rng.NextDouble()) * n.QueueEverySec;
                }
                var into = now - queueFrom;
                if (into < 0) return 0;
                return (into < rise ? into / rise : Math.Max(0, 1 - (into - rise) / fall)) * n.QueuePeakMs / 1000;
            }
            // A recorded network: each packet takes its recorded place's delay, loss or doubling, and no dice are drawn.
            var replay = s.Network.Replay?.Fresh();
            var ownPeriod = s.Codec switch
            {
                SimCodec.Opus2_5 => 120.0 / SampleRate,
                SimCodec.Opus10 => 480.0 / SampleRate,
                SimCodec.Opus20 => 960.0 / SampleRate,
                SimCodec.Custom64 => 64.0 / SampleRate,
                SimCodec.Custom128 => 128.0 / SampleRate,
                _ => Math.Min(sendCard.Frames, 233) / (double)SampleRate, // PCM: a packet a callback, cut at the standard frame
            };
            sender.PacketTapForTest = (bytes, _) =>
            {
                if (replay is not null)
                {
                    var arrivals = replay.ArrivalsFor(bytes, now, s.Network.BaseMs / 1000, ownPeriod);
                    if (arrivals.Count == 0 && RemPacket.TryReadHeader(bytes, out var kind, out ushort _, out uint _) && kind == RemPacketType.Audio) lost++;
                    foreach (var when in arrivals) At(when, () => receiver.InjectExternalPacket(bytes, bytes.Length, SenderAt));
                    return;
                }
                // Loss in bursts: once one starts, every packet is lost until it ends.
                if (s.Network.BurstLossPercent > 0)
                {
                    if (!inLossBurst && rng.NextDouble() * 100 < s.Network.BurstLossPercent) inLossBurst = true;
                    if (inLossBurst)
                    {
                        lost++;
                        if (rng.NextDouble() < 1 / Math.Max(1, s.Network.BurstLossLength)) inLossBurst = false;
                        return;
                    }
                }
                if (rng.NextDouble() * 100 < s.Network.LossPercent) { lost++; return; }
                // Networks keep order (queues are first in, first out); now and then a packet is overtaken by the next.
                var arrival = Math.Max(Arrival(s.Network, now, rng) + QueueDelay(), lastArrival);
                if (rng.NextDouble() * 100 < s.Network.ReorderPercent) arrival += 0.003;
                else lastArrival = arrival;
                At(arrival, () => receiver.InjectExternalPacket(bytes, bytes.Length, SenderAt));
                if (rng.NextDouble() * 100 < s.Network.DuplicatePercent)
                    At(arrival + 0.0005, () => receiver.InjectExternalPacket(bytes, bytes.Length, SenderAt));
            };

            // Push to talk, when the run has it: the gate configured, and the key pressed and released at its moments
            // through the real key logic. The recording tap is kept: each block's time, lane and loudest sample.
            var recorded = new List<(double Time, RenderRoute Lane, float Peak)>();
            if (s.PushToTalk is { } ptt)
            {
                sender.PushToTalk.Configure(ptt.Settings);
                var keys = new PushToTalkKeyState();
                keys.Configure(ptt.Settings.HoldToSpeak, ptt.Settings.DoubleTapToHold, ptt.Settings.DoubleTapSpeed);
                foreach (var (at, down) in ptt.Key)
                    At(at, () =>
                    {
                        var ms = (long)Math.Round(now * 1000);
                        if (down ? keys.Press(ms) : keys.Release(ms)) sender.PushToTalk.SetKeyTalking(keys.Talking);
                    });
                sender.OnSentSamples = (samples, lane) =>
                {
                    var peak = 0f;
                    foreach (var f in samples.Span) peak = Math.Max(peak, Math.Abs(f));
                    recorded.Add((now, lane, peak));
                };
            }

            // The signal, generated on the sending card's own clock.
            var sendRate = SampleRate * (1 + sendCard.DriftPpm * 1e-6);
            var wasapiSendRate = SampleRate * (1 + wasapiSendCard.DriftPpm * 1e-6);
            var toneOnWasapi = sendBoth && s.ToneOnWasapiLane;
            if (s.Music && (s.ChunkSeconds > 0 || s.CaptureDevices is not null || s.PushToTalk is not null || !s.DelayClicks))
                throw new ArgumentException("the music-like signal is listened to whole, with its delay clicks, fed straight to the lane: no stretches, capture devices or push to talk");
            var tone = new Signal(toneOnWasapi ? wasapiSendRate : sendRate, s);

            // Sender card callbacks. The block's samples were captured during the period before the callback.
            var sendPeriod = sendCard.Frames / sendRate;
            var silentSend = new float[sendCard.Frames * 2];
            // In "both", the lane the tone isn't on can carry a tone of its own, so each lane can be heard apart.
            var otherLane = sendBoth && s.PushToTalk is { OtherLaneTone: true };
            long otherCaptured = 0;
            float[] OtherTone(int frames)
            {
                var block = new float[frames * 2];
                for (var i = 0; i < frames; i++)
                {
                    var v = (float)(SimPushToTalk.OtherLaneLevel * Math.Sin(2 * Math.PI * SimPushToTalk.OtherLaneHz * (otherCaptured + i) / SampleRate));
                    block[2 * i] = v;
                    block[2 * i + 1] = v;
                }
                otherCaptured += frames;
                return block;
            }
            // The capture side in the loop (2026-09-30): simulated devices opened by the sender as it opens real ones - push
            // mode for one, the mixing engine for several or for a program's own sound - instead of blocks fed to the lane.
            var inputs = new List<(SimCaptureDevice Device, SimCaptureInput Input)>();
            var listenRate = tone.Rate;
            var capturePath = "fed to the lane";
            if (s.CaptureDevices is { Count: > 0 } devices)
            {
                if (sendAsio) throw new ArgumentException("simulated capture devices feed the WASAPI lane: the run must send WASAPI only");
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
                CaptureDevicesForTest.ManualMixTick = true;
                sender.WasapiCaptureLatencyMsForTest = null;   // the devices say their own
                sender.Configure(specs);
                sender.Start();
                // Each callback brings the frames its device captured up to that moment, by the callback's number: a
                // device that stops loses what it would have captured, as a real one does.
                foreach (var (d, input) in inputs) ScheduleCard(d.Card, d.Period, s.Seconds, rng, At, input.Deliver);
                // The mixing engine's 10 ms tick, woken by Windows' timer a little late each time, as its own thread is.
                capturePath = sender.WasapiPushModeForTest ? "push mode" : sender.WasapiMixerForTest is not null ? "the mixing engine" : "none";
                if (sender.WasapiMixerForTest is { } mixer)
                {
                    ScheduleCard(new SimCard(480, JitterMs: s.MixTickJitterMs), MixingEngine.MixTickMs / 1000.0, s.Seconds, rng, At, mixer.MixTickForTest);
                    // Out of the mixer, the sound runs on the mix clock (this run's own time).
                    listenRate = SampleRate;
                }
                else
                {
                    // Push mode passes the device's own samples on: a sound made on the device's clock comes out at its
                    // pace; the real world's sound (a microphone's), in real time.
                    var toneDevice = devices.FirstOrDefault(d => d.Tone) ?? devices[0];
                    listenRate = toneDevice.LockedToDevice ? SampleRate * (1 + toneDevice.Card.DriftPpm * 1e-6) : SampleRate;
                }
            }
            else ScheduleCard(sendCard, sendPeriod, s.Seconds, rng, At, () =>
            {
                var block = toneOnWasapi ? (otherLane ? OtherTone(sendCard.Frames) : silentSend) : tone.Block(sendCard.Frames);
                if (sendAsio) sender.FeedAsioLaneForTest(block);
                else sender.FeedDefaultLaneForTest(block);
            });
            // In "both", the WASAPI lane runs too, at its own 10 ms cadence, as a real second stream - silent unless it is
            // the one carrying the tone.
            if (sendBoth)
            {
                var silent = new float[wasapiSendCard.Frames * 2];
                ScheduleCard(wasapiSendCard, wasapiSendCard.Frames / wasapiSendRate, s.Seconds, rng, At,
                    () => sender.FeedDefaultLaneForTest(toneOnWasapi ? tone.Block(wasapiSendCard.Frames) : otherLane ? OtherTone(wasapiSendCard.Frames) : silent));
            }
            // A DAW track on the plugin lane: 440 Hz in 10 ms blocks, on the route the capture lanes leave free.
            if (s.PushToTalk is { PluginTrack: true })
            {
                sender.UsePluginLaneForTest(sendAsio ? RenderRoute.Mixed : RenderRoute.AsioLane);
                sender.SetPluginSendActive(true);
                long pluginCaptured = 0;
                ScheduleCard(new SimCard(480, JitterMs: 0.5), 0.010, s.Seconds, rng, At, () =>
                {
                    var block = new float[480 * 2];
                    for (var i = 0; i < 480; i++)
                    {
                        var v = (float)(SimPushToTalk.PluginLevel * Math.Sin(2 * Math.PI * SimPushToTalk.PluginHz * (pluginCaptured + i) / SampleRate));
                        block[2 * i] = v;
                        block[2 * i + 1] = v;
                    }
                    pluginCaptured += 480;
                    sender.SubmitPluginBlock(block);
                });
            }

            // Receiver cards: the output under test, and in "both" the WASAPI output too - each listened to.
            var receiveRate = SampleRate * (1 + receiveCard.DriftPpm * 1e-6);
            var receivePeriod = receiveCard.Frames / receiveRate;
            var wasapiReceiveRate = SampleRate * (1 + wasapiReceiveCard.DriftPpm * 1e-6);
            var wasapiReceivePeriod = wasapiReceiveCard.Frames / wasapiReceiveRate;
            var main = new Listener(listenRate, receiveRate, s.ChunkSeconds);
            var wasapiListener = both ? new Listener(listenRate, wasapiReceiveRate, s.ChunkSeconds) : null;
            // The WASAPI output stage, one per WASAPI output, when asked for: the engine read by the producer every 10 ms,
            // the card pulling from the stage.
            var stages = new Dictionary<RenderRoute, MultiOutputPlayout.SimulatedStage>();
            // Each stage's level, integrated over time at every feed and pull (area in ms x s, since when): its true time-averaged
            // level, measured the same way whatever the stage does inside.
            var stageArea = new Dictionary<MultiOutputPlayout.SimulatedStage, double[]>();
            void NoteStageLevel(MultiOutputPlayout.SimulatedStage st)
            {
                if (!stageArea.TryGetValue(st, out var a)) stageArea[st] = a = [0, now];
                a[0] += st.BufferedMs * (now - a[1]);
                a[1] = now;
            }
            IWaveProvider SourceFor(RenderRoute r)
            {
                var engineSide = receiver.SimulationOutput(r);
                var wasapiOutput = r == RenderRoute.WasapiLane || (r == RenderRoute.Mixed && s.Config == SimConfig.WasapiOnly);
                if (!s.WasapiOutputStage || !wasapiOutput) return engineSide;
                var stage = new MultiOutputPlayout.SimulatedStage($"simulated {r} card");
                stages[r] = stage;
                return stage;
            }
            void ScheduleOutput(RenderRoute r, SimCard card, double period, Listener listener)
            {
                var provider = SourceFor(r);
                var readBytes = new byte[card.Frames * 8];
                // What this card has played, as a real card's clock reports it: each block from one card buffer after it
                // was taken, for its own length.
                var played = new PlayedClock();
                if (provider is MultiOutputPlayout.SimulatedStage measured) measured.PlayedSeconds = () => played.At(now);
                ScheduleCard(card, period, s.Seconds, rng, At, () =>
                {
                    Array.Clear(readBytes);
                    if (provider is MultiOutputPlayout.SimulatedStage pulled) NoteStageLevel(pulled);
                    provider.Read(readBytes, 0, readBytes.Length);
                    played.Taken(now + period, readBytes.Length / 8.0 / SampleRate);
                    // Handed to the card now; heard one card buffer later.
                    listener.Heard(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(readBytes), now + period);
                });
            }
            ScheduleOutput(route, receiveCard, receivePeriod, main);
            if (wasapiListener is not null) ScheduleOutput(RenderRoute.WasapiLane, wasapiReceiveCard, wasapiReceivePeriod, wasapiListener);
            // The producer that feeds each stage: MultiOutputPlayout.ProduceLoop, 10 ms of the engine a tick on the machine's
            // own clock (a millisecond of timer jitter), handed to the stage whatever its card is doing.
            foreach (var (stageRoute, stage) in stages)
            {
                var engineSide = receiver.SimulationOutput(stageRoute);
                var frame = new byte[MultiOutputPlayout.SimulatedStage.ProducerFrameBytes];
                ScheduleCard(new SimCard(480, JitterMs: 1), MultiOutputPlayout.SimulatedStage.ProducerPeriodSec, s.Seconds, rng, At, () =>
                {
                    var produced = engineSide.Read(frame, 0, frame.Length);
                    NoteStageLevel(stage);
                    if (produced > 0) stage.Feed(frame, 0, produced);
                });
            }

            // The lowest-delay auto-tune's "keep dropouts rare" tuner, once a simulated second, as the main window's tick runs
            // it: the output's gaps that second in, the buffer out, applied the soft way the auto-tune's slider moves are.
            var tuner = new DropoutTuner();
            var tunedMs = s.BufferMs;
            long lastGaps = 0;
            var moves = new System.Text.StringBuilder();
            if (s.LowestDelayTune)
            {
                for (var sec = 1; sec <= (int)s.Seconds; sec++)
                {
                    var at = sec;
                    At(sec + 0.5, () =>
                    {
                        var gaps = route == RenderRoute.Mixed ? receiver.DropoutGaps : receiver.DropoutGapsFor(route);   // as the app counts them
                        var next = tuner.Tick((int)(gaps - lastGaps), tunedMs, Math.Max(1, (int)Math.Ceiling(receivePeriod * 1000)), 200);   // the app's floor: the output's period
                        lastGaps = gaps;
                        if (next == tunedMs) return;
                        moves.Append($"{tunedMs}>{next}@{at}s ");
                        tunedMs = next;
                        if (route == RenderRoute.Mixed) receiver.SetMaxLatencyMsSoft(next);
                        else receiver.SetMaxLatencyMsSoftFor(route, next);
                    });
                }
            }

            // One move of the buffer the auto-tune's way (the soft setter), part way through: the descent under test.
            // The slider moved by hand (the hard setter), part way through.
            // Anything else the run does to the receiver at a moment of its own (a slider on the other lane, say).
            foreach (var (at, act) in s.ReceiverActions ?? []) At(at, () => act(receiver));

            if (s.HandTargetMs > 0)
                At(s.HandTargetAtSecond, () => receiver.SetMaxLatencyMsFor(route, s.HandTargetMs));

            if (s.SoftTargetMs > 0)
                At(s.SoftTargetAtSecond, () =>
                {
                    if (route == RenderRoute.Mixed) receiver.SetMaxLatencyMsSoft(s.SoftTargetMs);
                    else receiver.SetMaxLatencyMsSoftFor(route, s.SoftTargetMs);
                });

            // The per-stream sizing, once a simulated second, as the main window's tick does - and what the auto-tune's
            // formula makes of the stream under test's own arrivals.
            var gapUsed = 0;
            var streamNames = new SortedSet<string>(StringComparer.Ordinal);
            IReadOnlyList<AudioReceiver.StreamNeedReport> lastReports = [];
            var laneTag = sendAsio ? "AsioLane" : "Mixed";
            for (var sec = 1; sec <= (int)s.Seconds; sec++)
                At(sec, () =>
                {
                    var reports = receiver.UpdatePerStreamNeeds(r => r == RenderRoute.AsioLane ? (int)Math.Ceiling(receivePeriod * 1000) : 10,
                        _ => s.ContinuousAutoTune || s.LowestDelayTune);   // as the app: only on an output whose auto-tune is on
                    lastReports = reports;
                    foreach (var rep in reports) if (rep.Packets > 0) streamNames.Add(rep.Stream);
                    var mine = reports.FirstOrDefault(r => r.Stream.Contains(laneTag, StringComparison.Ordinal) && r.Packets > 0);
                    if (mine is not null) gapUsed = mine.GapUsedMs;
                });

            // The main window's once-a-second work (the auto-tune's evidence, the readout) and its auto-tune tick, when the
            // run asks for either. A heartbeat's round trip goes over the same network, on dice of its own so the audio's
            // packets meet exactly what they would without it.
            SimMainWindow? window = null;
            if (s.ContinuousAutoTune || s.Diagnostics)
            {
                var pingRng = new Random(s.Seed + 7919);
                double ReportedOutput(RenderRoute r) => r switch
                {
                    // An ASIO driver states its buffer. A WASAPI output is measured through its stage, as the app measures it
                    // (the stage's MeasuredOutputMs); until it is, its engine period, doubled by DeviceLatency's rule.
                    RenderRoute.AsioLane => receiveCard.Frames * 1000.0 / SampleRate,
                    _ => stages.TryGetValue(s.Config == SimConfig.WasapiOnly ? RenderRoute.Mixed : RenderRoute.WasapiLane, out var measuredStage)
                         && measuredStage.MeasuredOutputMs >= 0
                        ? measuredStage.MeasuredOutputMs
                        : DeviceLatency.RenderMs((both ? wasapiReceiveCard : receiveCard).Frames * 1000.0 / SampleRate, 5),
                };
                double Queue(RenderRoute r) => r == RenderRoute.AsioLane ? 0
                    : stages.TryGetValue(s.Config == SimConfig.WasapiOnly ? RenderRoute.Mixed : RenderRoute.WasapiLane, out var st) ? st.BufferedMs : 0;
                double Ping() => (Arrival(s.Network, now, pingRng) - now + Arrival(s.Network, now, pingRng) - now) * 1000;
                window = new SimMainWindow(receiver, sender, s.Config, s.ContinuousAutoTune, s.AutoTuneIntervalSec, s.BufferMs,
                    wasapiLaneMs, ReportedOutput, Queue, Ping);
                // The lowest-delay tune has the listened output: the usual tune stands aside there, as the app's does.
                if (s.LowestDelayTune) window.StandAside.Add(route);
                for (var sec = 1; sec <= (int)s.Seconds; sec++) At(sec + 0.25, () => window.EverySecond(now));
                if (s.ContinuousAutoTune)
                    for (var t = s.AutoTuneIntervalSec; t <= s.Seconds; t += s.AutoTuneIntervalSec) At(t + 0.3, () => window.TuneTick(now));
            }

            // Anything else the run watches each second (SimLogs: the log's rule fed the receiver's real gaps). It draws no dice.
            if (s.EverySecondDo is { } everySecond)
                for (var sec = 1; sec <= (int)s.Seconds; sec++) At(sec + 0.26, () => everySecond(now, receiver, lastReports));

            // Each second: the listened output's underruns, the post-decode probe, the WASAPI stage's reading.
            var underrunsBySecond = new List<long>();
            var trimsBySecond = new List<long>();
            var driftPpmBySecond = new List<double>();
            var postDecode = new List<(float, float)>();
            var stageBySecond = new List<WasapiOutputStage>();
            var replicasBySecond = new List<List<(RenderRoute, RenderRoute, bool, double, double, bool)>>();
            var stageShortReadsBySecond = new List<long>();
            var stageLevelBySecond = new List<double>();
            for (var sec = 1; sec <= (int)s.Seconds; sec++)
                At(sec + 0.2, () =>
                {
                    underrunsBySecond.Add(receiver.TuneBlockingUnderrunsFor(countersRoute));
                    trimsBySecond.Add(receiver.TrimFireCount);
                    var ratios = receiver.DriftRatiosForTest(countersRoute);
                    driftPpmBySecond.Add(ratios.Length > 0 ? (ratios[0] - 1) * 1e6 : double.NaN);
                    if (DiagnosticsGate.Enabled)
                        postDecode.Add((receiver.TakeMaxPostDecodeStepCrossBuffer(), receiver.TakeMaxPostDecodeStepWithinBuffer()));
                    if (stages.Count > 0)
                    {
                        stageBySecond.Add(stages.Values.First().Snapshot());
                        stageShortReadsBySecond.Add(stages.Values.Sum(st => st.ShortReads));
                        // The first stage's time-averaged level over the second just gone (ms).
                        var first = stages.Values.First();
                        NoteStageLevel(first);
                        stageLevelBySecond.Add(stageArea[first][0]);
                        stageArea[first][0] = 0;
                    }
                    replicasBySecond.Add(receiver.ReplicasForTest());
                    receiver.NoteNetworkSecond();
                });

            // ---- run ----
            while (queue.TryDequeue(out var action, out var when))
            {
                if (when.Item1 > s.Seconds + 1) break;
                now = when.Item1;
                action();
            }

            // Listened to from AnalyseFromSecond on (a tuner's settled stretch), or all of it, or a stretch at a time.
            var findings = main.Finish(s.AnalyseFromSecond);
            var wasapiFindings = wasapiListener?.Finish(s.AnalyseFromSecond);
            var underruns = receiver.TuneBlockingUnderrunsFor(countersRoute);
            var (fec, unrecovered, concealed) = receiver.CodecRepairsForTest;
            NetworkRecording? recording = null;
            if (s.RecordNetwork)
            {
                receiver.NoteNetworkSecond();
                recording = receiver.TakeNetworkRecording(
                [
                    new("machine", "the simulator"),
                    new("scenario", s.Name),
                    new("network", s.Network.Name),
                    new("one-way ms", s.Network.BaseMs.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    new("configuration", s.Config.ToString()),
                    new("codec", s.Codec.ToString()),
                    new("receive card frames", receiveCard.Frames.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ]);
            }
            return new SimResult(s, findings, underruns, sender.PacketsSent, lost, fec, unrecovered, concealed,
                s.KeepAudio && s.ChunkSeconds <= 0 ? main.Output.ToArray() : null, wall.Elapsed.TotalSeconds, gapUsed, (int)Math.Ceiling(receivePeriod * 1000))
                { Streams = streamNames.ToArray(), LateDropped = receiver.LateDroppedForTest, Recording = recording,
                  Music = s.Music ? SimMusic.Analyse(main.Output, main.Times, listenRate, s.AnalyseFromSecond) : null,
                  CapturePath = capturePath, CaptureDrift = sender.WasapiMixerForTest?.DriftForTest ?? [], PcmConcealed = receiver.PcmConcealedForTest, LongGapRearms = receiver.LongGapRearmsForTest, ReorderWaits = receiver.ReorderForTest.Waits, ReorderFilled = receiver.ReorderForTest.Filled,
                  TunedMs = tunedMs, TunerMoves = moves.ToString().Trim(),
                  WasapiOutput = wasapiFindings, WasapiOutputUnderruns = both ? receiver.TuneBlockingUnderrunsFor(RenderRoute.WasapiLane) : 0,
                  SessionsOpened = receiver.SessionsOpenedCount, Window = window, UnderrunsBySecond = underrunsBySecond,
                  Stretches = main.Stretches, WasapiStretches = wasapiListener?.Stretches ?? [], PostDecodeSteps = postDecode,
                  StageBySecond = stageBySecond, ReplicasBySecond = replicasBySecond, StageShortReads = stages.Values.Sum(st => st.ShortReads), StageShortReadsBySecond = stageShortReadsBySecond, StageLevelBySecond = stageLevelBySecond, TrimFires = receiver.TrimFireCount, SnapCount = receiver.SnapCount, SnapSkippedMs = receiver.SnapSkippedBytes / (SampleRate * 8 / 1000.0), TrimDroppedMs = receiver.TrimDropBytes / (SampleRate * 8 / 1000.0),
                  TrimsBySecond = trimsBySecond, DriftPpmBySecond = driftPpmBySecond,
                  AudioTimes = s.KeepAudio && s.ChunkSeconds <= 0 ? main.Times.ToArray() : [],
                  WasapiAudio = s.KeepAudio && s.ChunkSeconds <= 0 ? wasapiListener?.Output.ToArray() : null,
                  WasapiAudioTimes = s.KeepAudio && s.ChunkSeconds <= 0 && wasapiListener is not null ? wasapiListener.Times.ToArray() : [],
                  Recorded = recorded };
        }
        finally
        {
            sender.PacketTapForTest = null;
            sender.OnSentSamples = null;
            StreamSession.ReorderWaitOffForTest = false;
            SessionPlayout.ConcealSeedForTest = null;
            AudioClock.UseSimulated(null);
            CaptureDevicesForTest.Open = null;
            CaptureDevicesForTest.ManualMixTick = false;
            DiagnosticsGate.Enabled = diagnosticsWere;
            JamulusStyle.Reset();
            CustomOpusPeers.Clear();
        }
    }

    internal static void ConfigureCodec(AudioSender sender, SimCodec codec)
    {
        // Tight latency is always on in the app (MainForm) and the service: PCM goes out one packet per callback.
        sender.SetTightLatency(true);
        switch (codec)
        {
            case SimCodec.Pcm:
                sender.ConfigureCodec(AudioTransportCodec.Pcm);
                sender.SetSendRate(SendRate.Standard);
                break;
            case SimCodec.Opus2_5: sender.ConfigureCodec(AudioTransportCodec.Opus, 120); break;
            case SimCodec.Opus10: sender.ConfigureCodec(AudioTransportCodec.Opus, 480); break;
            case SimCodec.Opus20: sender.ConfigureCodec(AudioTransportCodec.Opus, 960); break;
            case SimCodec.Custom128:
            case SimCodec.Custom64:
                sender.ConfigureCodec(AudioTransportCodec.Opus, 120);
                if (!OpusCustomNative.TryLoad()) throw new InvalidOperationException(OpusCustomNative.LoadMessage);
                JamulusStyle.FrameSamples = codec == SimCodec.Custom128 ? 128 : 64;
                CustomOpusPeers.Note(ReceiverAt.Address); // the receiving copy has said it can decode them
                // ...and goes on saying so, as a copy playing through ASIO does in every Format packet. One process plays
                // both ends, so this copy's own Format packets carry it back to the sender: without it the sender forgets
                // the receiver three seconds in and drops back to Opus - which only a run on the wall clock lasts long
                // enough to hear (SIMULATOR WALL CLOCK, 2026-10-01, when the flag stopped riding on a lab switch).
                JamulusStyle.PlaysThroughAsio = true;
                break;
        }
    }

    /// <summary>Callbacks of a card: one per period on its own clock, each up to JitterMs late (never before the one
    /// before it), and during a stall held back and then fired all at once, as a late driver thread catches up. A one-off
    /// stall's callbacks are dropped instead: the device stopped, and comes back where the time now is.</summary>
    internal static void ScheduleCard(SimCard card, double period, double seconds, Random rng, Action<double, Action> at, Action callback) =>
        ScheduleCard(card, period, seconds, rng, at, _ => callback());

    /// <summary>The same, telling each callback its number (the first is 1).</summary>
    internal static void ScheduleCard(SimCard card, double period, double seconds, Random rng, Action<double, Action> at, Action<int> callback)
    {
        var count = (int)(seconds / period);
        double last = 0;
        var onceFrom = card.StallAtSec;
        var onceTo = card.StallAtSec + card.StallOnceMs / 1000;
        for (var k = 1; k <= count; k++)
        {
            var t = k * period + card.PhaseMs / 1000 + (card.JitterMs > 0 ? rng.NextDouble() * card.JitterMs / 1000 : 0);
            if (card.StallOnceMs > 0 && t >= onceFrom && t < onceTo) continue;
            if (card.StallEverySec > 0 && card.StallMs > 0)
            {
                var into = t % card.StallEverySec;
                var stallStart = card.StallEverySec - card.StallMs / 1000;
                if (into >= stallStart) t += card.StallEverySec - into;
            }
            t = Math.Max(t, last);
            last = t;
            var number = k;
            at(t, () => callback(number));
        }
    }

    /// <summary>When a packet sent at <paramref name="sent"/> arrives.</summary>
    internal static double Arrival(SimNetwork n, double sent, Random rng)
    {
        var jitter = n.JitterMs > 0 ? Math.Min(-Math.Log(1 - rng.NextDouble()) * n.JitterMs, n.JitterMs * 6) : 0;
        if (n.HiccupPercent > 0 && rng.NextDouble() * 100 < n.HiccupPercent) jitter += rng.NextDouble() * n.HiccupMs;
        var t = sent + (n.BaseMs + jitter) / 1000;
        if (n.LumpMs > 0)
        {
            var lump = n.LumpMs / 1000;
            t = Math.Ceiling(t / lump) * lump;
        }
        if (n.StallEverySec > 0 && n.StallMs > 0)
        {
            var into = t % n.StallEverySec;
            var stallStart = n.StallEverySec - n.StallMs / 1000;
            if (into >= stallStart) t += n.StallEverySec - into;
        }
        if (n.StallOnceMs > 0 && t >= n.StallAtSec && t < n.StallAtSec + n.StallOnceMs / 1000) t = n.StallAtSec + n.StallOnceMs / 1000;
        return t;
    }
}
