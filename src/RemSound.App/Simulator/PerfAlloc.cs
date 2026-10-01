using System.Diagnostics;
using RemSound.Core;

namespace RemSound.App.Simulator;

/// <summary>What one steady stretch allocated, call by call: the sender's capture blocks, the receiver's audio packets and
/// format packets, the output cards' reads and the WASAPI output stage's producer, and the once-a-second work.</summary>
internal sealed record AllocResult(SimConfig Config, SimCodec Codec, int Peers, double Seconds,
    long SendBytes, long SendCalls, long AudioBytes, long AudioPackets, long FormatBytes, long FormatPackets,
    long RenderBytes, long RenderCalls, long ProducerBytes, long ProducerCalls, long EverySecondBytes, int Gen0Collections)
{
    /// <summary>How many audio packets, and how many output reads, allocated anything at all.</summary>
    public long AllocatingAudioPackets { get; init; }
    public long AllocatingReads { get; init; }
    public long Reads => RenderCalls + ProducerCalls;

    public int Lanes => Config == SimConfig.Both ? 2 : 1;
    public int Streams => Peers * Lanes;
    public double SendPerLaneSecond => SendBytes / (double)(Peers * Lanes) / Seconds;
    public double FormatPerPacket => FormatPackets == 0 ? 0 : FormatBytes / (double)FormatPackets;
    public double FormatPerStreamSecond => FormatBytes / (double)Streams / Seconds;
    public double EverySecondPerStreamSecond => EverySecondBytes / (double)Streams / Seconds;

    public string Plain => $"{PerfBench.Name(Config)}, {SimLab.Name(Codec)}, {(Peers == 1 ? "one peer" : $"{Peers} peers")}: "
        + $"{AudioPackets} audio packets {AudioBytes} bytes, {RenderCalls + ProducerCalls} output reads {RenderBytes + ProducerBytes} bytes, "
        + $"the sender {SendPerLaneSecond:0} bytes a lane a second ({SendCalls} blocks), {FormatPackets} format packets {FormatPerPacket:0} bytes each, "
        + $"the once-a-second work {EverySecondPerStreamSecond:0} bytes a stream a second; {Gen0Collections} collections in {Seconds:0} simulated s";
}

/// <summary>
/// ALLOCATIONS ON THE AUDIO PATH (2026-09-28). Once a stream is running, every allocation on the paths that run per block
/// or per packet is garbage the collector must come for - and a collection is a pause, which on an audio thread is a
/// glitch. Measured with the allocating thread's own counter (GC.GetAllocatedBytesForCurrentThread) around each call into
/// RemSound, over eight simulated seconds after twelve of warm-up (the rig allocates nothing of its own in that time).
///
/// <para>What was found, and the bars set from it: an audio packet, an output card's read and the WASAPI output stage's
/// producer allocate nothing, in every configuration and way of sending, once the WASAPI output stage's input buffer has grown
/// once to its first drift correction (at the first ten-second window). What does allocate is the format packet,
/// four a second per stream: 200 bytes each on the receiver - 160 in RemPacket.TryReadFormat (it builds two AudioFormatInfo
/// records, a default and the real one, and copies the eight-byte password fingerprint into a new array), and 40 in
/// AudioReceiver.HandleFormat, whose lambda for a NEW session's onFramesQueued captures a local declared at the top of the
/// method, so the compiler builds that closure on every call, the same-format repeats and strangers' packets included
/// (proved 2026-09-28 by moving the lambda into a method of its own: 160 bytes; then put back) - and 64 bytes each on the
/// sender (SenderLane.EnsureFormatPacketSent builds a new AudioFormatInfo for every re-announce); and the main window's
/// once-a-second calls (the per-stream sizing's report builds strings and lists). None of that is per block or per packet,
/// and at about a kilobyte a second per stream it wakes the collector never in ten simulated seconds.</para>
/// </summary>
internal static class PerfAlloc
{
    /// <summary>Nothing, per audio packet and per output read: no more than one call in a hundred may allocate anything -
    /// a scratch array growing to a new largest block when a drift correction first needs one (measured 2026-09-28: the
    /// WASAPI output stage's input buffer, once, at a drift window, 3,880 bytes). Something allocated per packet or per read
    /// would be every one of them.</summary>
    public const double AllocatingCallsAtMost = 0.01;
    /// <summary>The sender, per lane per second: its four format re-announces (64 bytes each measured).</summary>
    public const double SenderBytesPerLaneSecond = 320;
    /// <summary>A format packet on the receiver (200 bytes measured).</summary>
    public const double FormatPacketBytes = 256;
    /// <summary>The once-a-second work, per stream per second (1.2 to 1.6 KB measured).</summary>
    public const double EverySecondBytesPerStreamSecond = 4096;

    /// <summary>Past the first of the drift loops' ten-second windows: when it closes, the WASAPI output stage's input buffer
    /// grows once to hold its first correction (3,880 bytes, measured 2026-09-28) - start-up, not steady state.</summary>
    public const double WarmSeconds = 12;
    public const double MeasureSeconds = 8;

    public static AllocResult Measure(SimConfig config, SimCodec codec, int peers) => PerfBench.OnOwnThread(() =>
    {
        using var rig = new PerfRig(new PerfRigOptions(config, codec));
        for (var i = 0; i < peers; i++) rig.Join(i, PerfSignal.Busy);
        rig.RunUntil(WarmSeconds);
        rig.Meter.Reset();
        var gen0 = GC.CollectionCount(0);
        rig.RunFor(MeasureSeconds);
        var m = rig.Meter;
        return new AllocResult(config, codec, peers, MeasureSeconds,
            m.Allocated(PerfCost.Send), m.Calls(PerfCost.Send), m.Allocated(PerfCost.DeliverAudio), m.Calls(PerfCost.DeliverAudio),
            m.Allocated(PerfCost.DeliverFormat), m.Calls(PerfCost.DeliverFormat), m.Allocated(PerfCost.Render), m.Calls(PerfCost.Render),
            m.Allocated(PerfCost.Producer), m.Calls(PerfCost.Producer), m.Allocated(PerfCost.EverySecond), GC.CollectionCount(0) - gen0)
        {
            AllocatingAudioPackets = m.AllocatingCalls(PerfCost.DeliverAudio),
            AllocatingReads = m.AllocatingCalls(PerfCost.Render) + m.AllocatingCalls(PerfCost.Producer),
        };
    });

    /// <summary>The allocation sites, measured on their own: RemPacket.TryReadFormat on a real format payload (the receiver's
    /// per-format-packet cost), and one AudioFormatInfo as the sender builds it for each re-announce.</summary>
    public static (long ReadFormat, long FormatRecord) Sites()
    {
        var (_, fingerprint) = RemSoundCrypto.ForPlainPassword("simulator");
        var format = new AudioFormatInfo(48000, 2, 16, 1, 4, 192_000, (int)AudioTransportCodec.Opus, 120, RenderRoute.AsioLane, 0.7);
        var payload = new byte[RemPacket.FormatPayloadWithCaptureSize];
        var length = RemPacket.WriteFormatPayload(payload, format, fingerprint);
        long read = 0, record = 0;
        for (var i = 0; i < 3; i++)   // the last of three, once everything is compiled
        {
            var a0 = GC.GetAllocatedBytesForCurrentThread();
            RemPacket.TryReadFormat(payload.AsSpan(0, length), out _, out _);
            read = GC.GetAllocatedBytesForCurrentThread() - a0;
            a0 = GC.GetAllocatedBytesForCurrentThread();
            var made = new AudioFormatInfo(48000, 2, 16, 1, 4, 192_000, (int)AudioTransportCodec.Opus, 120 + i, RenderRoute.AsioLane, 0.7);
            record = GC.GetAllocatedBytesForCurrentThread() - a0;
            GC.KeepAlive(made);
        }
        return (read, record);
    }
}
