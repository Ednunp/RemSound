using RemSound.Core;

namespace RemSound.Receiver;

/// <summary>
/// THE METRONOME ON RemSound's OUTPUTS (PLAN-metronome-2026-09-28, "Never on the network, never captured"). The click is
/// made here, on each output, in the render call that knows when its samples will be heard - after everything that is
/// sent, so it never goes to anyone. Each WASAPI output and the ASIO output keep a <see cref="MetronomeClickLane"/> of their
/// own (their own ear clock, their own delay); what they place clicks from is the one schedule the window publishes.
///
/// <para><b>Never captured.</b> An output that this machine also SENDS a loopback of would put the click into the stream
/// that goes to everyone. The window names those outputs (<see cref="SetLoopbackSent"/>) and the click is not played on
/// them - only on the others.</para>
///
/// <para>Also the recording's click: with "Include the click in RemSound's recordings" ticked, the recorder adds the click to
/// each block the recording lane reads, placed as that lane's output plays it (<see cref="MixForRecording"/>).</para>
/// </summary>
public sealed class MetronomeOutputs
{
    private volatile MetronomeSchedule? schedule;
    private volatile HashSet<string> loopbackSent = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What to place clicks from; null plays none (the metronome is stopped, or heard in the DAW).</summary>
    public MetronomeSchedule? Schedule { get => schedule; set => schedule = value; }

    /// <summary>The WASAPI outputs this machine sends a loopback of: the click never plays on them.</summary>
    public void SetLoopbackSent(IEnumerable<string> deviceIds) => loopbackSent = new HashSet<string>(deviceIds, StringComparer.OrdinalIgnoreCase);

    public bool IsLoopbackSent(string deviceId) => loopbackSent.Contains(deviceId);

    private volatile bool record;
    private long recordDelayBits;

    /// <summary>"Include the click in RemSound's recordings": the recorder adds the click to what it records, as heard.</summary>
    public bool Record { get => record; set => record = value; }

    /// <summary>From the recording lane's read to what it read being heard (its output's delay), so the click lands in the
    /// recording where the listener heard it against the peers' audio. Set by the window once a second.</summary>
    public double RecordDelayUs
    {
        get => BitConverter.Int64BitsToDouble(Interlocked.Read(ref recordDelayBits));
        set => Interlocked.Exchange(ref recordDelayBits, BitConverter.DoubleToInt64Bits(value));
    }

    /// <summary>The recording's own click lane: its ear clock is the recording lane's reads.</summary>
    public MetronomeClickLane RecordLane { get; } = new("recording");

    /// <summary>Add this recorded block's clicks to it (stereo, 48 kHz), when the click is recorded. Called on the recording
    /// lane's render thread, with the block that lane just read.</summary>
    public void MixForRecording(Span<float> interleaved)
    {
        if (!record) return;
        Mix(RecordLane, null, interleaved, 2, MetronomeSound.SampleRate, RecordDelayUs);
    }

    /// <summary>Clicks placed on each output since it opened, by name - for the log and the gate.</summary>
    public long OnsetsOn(MetronomeClickLane lane) => lane.OnsetsPlaced;

    /// <summary>Mix one block's clicks into an output's block. Returns true when anything was added (the caller clamps).
    /// <paramref name="deviceId"/> is the WASAPI device (null for ASIO), checked against the outputs sent as loopback.</summary>
    public bool Mix(MetronomeClickLane lane, string? deviceId, Span<float> interleaved, int channels, int sampleRate, double outputDelayUs)
    {
        var s = schedule;
        if (deviceId is not null && loopbackSent.Contains(deviceId)) s = null;
        var now = (s?.Clock ?? MetronomeClock.NowUs)();
        var before = lane.OnsetsPlaced;
        var sounding = lane.Sounding;
        lane.Mix(interleaved, channels, sampleRate, now, outputDelayUs, s);
        return sounding || lane.OnsetsPlaced != before;
    }
}
