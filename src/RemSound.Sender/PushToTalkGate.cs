using System.Diagnostics;
using RemSound.Core;

namespace RemSound.Sender;

/// <summary>Which of the sender's three lanes a block of audio is on, as push to talk sees them: your WASAPI sources,
/// your ASIO sources, or a DAW track sent through the plugin.</summary>
internal enum SendLaneKind
{
    WasapiCapture,
    AsioCapture,
    Plugin,
}

/// <summary>
/// PUSH TO TALK, AT THE SEND LANES (Ed, 2026-09-28). Sending is the inner airlock; this is the outer one, which opens and
/// shuts at once. It works on the audio just before it is encoded, so the stream to every peer keeps running the whole
/// time and carries silence while shut - opening costs nothing but the ordinary delay, and the receivers' buffers and
/// auto-tunes never notice. Opening and shutting fade over <see cref="FadeMs"/>, so neither clicks.
///
/// <para>Which lanes it holds: the WASAPI lane, the ASIO lane or both, as the profile says; and the plugin's DAW track unless
/// the plugin bypass is ticked. What opens it: the key (the window hands in <see cref="SetKeyTalking"/>), or the push to
/// talk path's own sound reaching the level - a peak at or above it opens the lane from that very sample (there is no
/// look-ahead, which would add delay), and it shuts <see cref="HangSeconds"/> after the last such peak. The other path, and
/// the DAW track in sound-level mode, cannot open it.</para>
///
/// <para>The recording follows what goes out unless the recording bypass is ticked; then it holds everything coming out
/// of the send channels (<see cref="RecordsUngated"/>). Off, or not on this lane's path, a block passes through untouched
/// and nothing is copied.</para>
/// </summary>
public sealed class PushToTalkGate
{
    public const double FadeMs = 5.0;
    public const double HangSeconds = 0.5;
    private const int SampleRate = 48000;
    private const int Channels = 2;
    private const float FadeStep = (float)(1000.0 / (FadeMs * SampleRate));

    private volatile PushToTalkSettings? settings;
    private volatile bool keyTalking;
    private long lastLoudTicks = long.MinValue / 2;
    private readonly object countGate = new();
    private bool open;
    private long opens;
    private long closes;

    /// <summary>The per-lane part: where its fade has got to, and the lane's own scratch, so the lanes (each on its own
    /// audio thread) never share one.</summary>
    internal sealed class Lane(SendLaneKind kind)
    {
        public readonly SendLaneKind Kind = kind;
        public float Gain = 1f;
        // Room for the largest block any lane is handed (a DAW's 4096-frame block), so the audio thread never allocates.
        public float[] Scratch = new float[4096 * Channels];
    }

    /// <summary>The profile's settings; null or off lets everything through.</summary>
    public void Configure(PushToTalkSettings? value)
    {
        settings = value?.Clone();
        if (value is not { Enabled: true }) keyTalking = false;
        lastLoudTicks = long.MinValue / 2;
    }

    public PushToTalkSettings? Settings => settings?.Clone();

    /// <summary>Key mode: talking or not, as the key's behaviour (hold, tap, double tap) decides.</summary>
    public void SetKeyTalking(bool talking) => keyTalking = talking;

    public bool Enabled => settings is { Enabled: true };

    /// <summary>True when the recording should hold everything the send channels carry, not only what goes out.</summary>
    public bool RecordsUngated => settings is not { Enabled: true } || settings.BypassForRecording;

    /// <summary>Open right now: off (everything goes), or talking by the key, or the sound reached the level within the
    /// hang time.</summary>
    public bool IsOpen
    {
        get
        {
            var s = settings;
            if (s is not { Enabled: true }) return true;
            return s.Trigger == PushToTalkTrigger.Key ? keyTalking : LevelOpenAt(AudioClock.GetTimestamp());
        }
    }

    /// <summary>How many times it has opened and shut since the last call - for the log, which the window writes.</summary>
    public (long Opens, long Closes) TakeTransitions()
    {
        lock (countGate)
        {
            var result = (opens, closes);
            opens = 0;
            closes = 0;
            return result;
        }
    }

    private bool LevelOpenAt(long ticks) => ticks - Interlocked.Read(ref lastLoudTicks) <= (long)(HangSeconds * Stopwatch.Frequency);

    private void NoteState(bool nowOpen)
    {
        lock (countGate)
        {
            if (nowOpen == open) return;
            open = nowOpen;
            if (nowOpen) opens++; else closes++;
        }
    }

    /// <summary>Is this lane one push to talk holds?</summary>
    private static bool Holds(PushToTalkSettings s, SendLaneKind kind) => kind switch
    {
        SendLaneKind.WasapiCapture => s.Path != PushToTalkPath.Asio,
        SendLaneKind.AsioCapture => s.Path != PushToTalkPath.Wasapi,
        _ => !s.BypassForPlugin,
    };

    /// <summary>
    /// One lane's block, gated. Returns the block itself when push to talk leaves it alone and its fade is fully open;
    /// otherwise the lane's scratch holding the gated copy. Called on the lane's own audio thread.
    /// </summary>
    internal ReadOnlyMemory<float> Apply(Lane lane, ReadOnlyMemory<float> block)
    {
        var s = settings;
        var input = block.Span;
        var frames = input.Length / Channels;
        var held = s is { Enabled: true } && Holds(s, lane.Kind);
        var target = true;
        var openFrom = 0;
        if (held)
        {
            if (s!.Trigger == PushToTalkTrigger.Key)
            {
                target = keyTalking;
            }
            else
            {
                var now = AudioClock.GetTimestamp();
                var blockStart = now - (long)(frames * (double)Stopwatch.Frequency / SampleRate);
                var openAtStart = LevelOpenAt(blockStart);
                if (lane.Kind == SendLaneKind.Plugin)
                {
                    // The DAW track follows; only the push to talk path's own sound opens it.
                    target = openAtStart || LevelOpenAt(now);
                }
                else
                {
                    var level = s.LevelLinear;
                    var first = -1;
                    var last = -1;
                    for (var f = 0; f < frames; f++)
                    {
                        var l = input[2 * f];
                        var r = input[2 * f + 1];
                        var peak = Math.Max(l < 0 ? -l : l, r < 0 ? -r : r);
                        if (peak < level) continue;
                        if (first < 0) first = f;
                        last = f;
                    }
                    if (last >= 0)
                    {
                        var lastLoud = blockStart + (long)((last + 1) * (double)Stopwatch.Frequency / SampleRate);
                        long seen;
                        while ((seen = Interlocked.Read(ref lastLoudTicks)) < lastLoud
                               && Interlocked.CompareExchange(ref lastLoudTicks, lastLoud, seen) != seen) { }
                    }
                    if (openAtStart) target = true;
                    else if (first >= 0) { target = true; openFrom = first; }
                    else target = false;
                    NoteState(target || LevelOpenAt(now));
                }
            }
            if (s.Trigger == PushToTalkTrigger.Key && lane.Kind != SendLaneKind.Plugin) NoteState(target);
        }

        // Fully open and staying so: the block itself, nothing copied.
        if (target && openFrom == 0 && lane.Gain >= 1f) return block;

        if (lane.Scratch.Length < input.Length) lane.Scratch = new float[Math.Max(input.Length, 2048)];
        var output = lane.Scratch.AsSpan(0, input.Length);
        var gain = lane.Gain;
        if (!target && gain <= 0f)
        {
            output.Clear();
        }
        else
        {
            for (var f = 0; f < frames; f++)
            {
                var want = target && f >= openFrom ? 1f : 0f;
                if (gain < want) gain = Math.Min(want, gain + FadeStep);
                else if (gain > want) gain = Math.Max(want, gain - FadeStep);
                output[2 * f] = input[2 * f] * gain;
                output[2 * f + 1] = input[2 * f + 1] * gain;
            }
        }
        lane.Gain = gain;
        return lane.Scratch.AsMemory(0, input.Length);
    }
}
