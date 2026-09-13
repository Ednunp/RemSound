using RemSound.Core;

namespace RemSound.App;

/// <summary>What the auto-tune had at one moment: both jitter buffer sliders and every lane's learned floor. The main
/// slider drives the Mixed route in a WASAPI-only configuration and the WASAPI lane when both kinds of output are
/// ticked; the ASIO slider drives the ASIO lane. So this one record covers all three configurations.</summary>
internal readonly record struct TuneSnapshot(DateTime AtUtc, int MainSliderMs, int AsioSliderMs, int MixedFloorMs, int WasapiFloorMs, int AsioFloorMs)
{
    public int FloorFor(RenderRoute route) => route switch
    {
        RenderRoute.WasapiLane => WasapiFloorMs,
        RenderRoute.AsioLane => AsioFloorMs,
        _ => MixedFloorMs,
    };

    public string Describe() =>
        $"jitter buffer {MainSliderMs} ms, ASIO jitter buffer {AsioSliderMs} ms, learned floors Mixed {MixedFloorMs} / WASAPI {WasapiFloorMs} / ASIO {AsioFloorMs} ms";
}

/// <summary>
/// WHAT THE TUNER HAD BEFORE THE SLEEP, SO A WAKE CAN PUT IT BACK.
///
/// <para>Ed, 2026-09-12, after three versions spent teaching the tuner which readings to believe after a wake: "RemSound
/// can detect a resume right? so just, stop and start the audio ... stop all the streams and restart them with the auto
/// tune before it went to sleep." The restart is <c>MainForm.RestartAudioAfterWake</c>. This is the "auto tune before it
/// went to sleep" half.</para>
///
/// <para><b>Why not simply the value at the moment of sleep.</b> The last minute before a sleep is the machine going
/// down, and the tuner reacts to it. Ed's laptop, 2026-09-12: he unplugged the Roger On, the tuner raised ASIO from 23
/// to 31 ms on the underruns that caused, and the machine hibernated twenty seconds later. On 2026-09-07 WASAPI was
/// raised from 36 to 41 ms in the very second the machine went down. And the tuner hunts: over the five minutes before
/// that sleep ASIO moved between 22 and 33 ms and WASAPI between 32 and 43. Any single instant is an accident of timing.
/// So the tune put back is the MEDIAN slider over the five minutes that ended a minute before the sleep, with the floors
/// as they stood at the end of those five minutes.</para>
///
/// <para>Samples are trimmed by COUNT, never by age. Nothing is recorded while the machine sleeps, so after five hours
/// asleep the samples from before it are still the newest ones there are — trimming by age would throw them all away on
/// the first tick after waking, exactly when they are needed.</para>
/// </summary>
internal sealed class TuneHistory
{
    /// <summary>About seven minutes at one sample a second: the five-minute window, the minute before the sleep that is
    /// ignored, and a little spare for the ticks between waking and the restart.</summary>
    public const int MaxSamples = 420;

    /// <summary>How much of the time before the sleep the median is taken over.</summary>
    public static readonly TimeSpan SettledSpan = TimeSpan.FromMinutes(5);

    /// <summary>The minute before the sleep is the machine going down — devices unplugged, the tuner reacting — and is
    /// not the tune anyone was listening to.</summary>
    public static readonly TimeSpan GoingToSleep = TimeSpan.FromSeconds(60);

    /// <summary>For this long after the restart, the restarted streams coming back and the outputs starting again put
    /// the tune back rather than wiping it. Long enough for a slow Wi-Fi reconnect and the peer noticing this machine is
    /// back; short enough that a genuinely new stream later is treated as new.</summary>
    public static readonly TimeSpan ReapplyAfterRestart = TimeSpan.FromSeconds(60);

    private readonly List<TuneSnapshot> samples = [];

    public int Count => samples.Count;

    public void Note(TuneSnapshot sample)
    {
        samples.Add(sample);
        if (samples.Count > MaxSamples) samples.RemoveRange(0, samples.Count - MaxSamples);
    }

    /// <summary>The tune to put back after a sleep that began at <paramref name="sleptAtUtc"/>, or null when nothing was
    /// recorded before it. If the app had not been running long enough to have a settled window, whatever was recorded
    /// before the sleep is used instead.</summary>
    public TuneSnapshot? BeforeSleep(DateTime sleptAtUtc)
    {
        var before = samples.Where(s => s.AtUtc <= sleptAtUtc).OrderBy(s => s.AtUtc).ToList();
        if (before.Count == 0) return null;
        var end = sleptAtUtc - GoingToSleep;
        var settled = before.Where(s => s.AtUtc <= end && s.AtUtc > end - SettledSpan).ToList();
        if (settled.Count == 0) settled = before;
        var last = settled[^1];
        return new TuneSnapshot(last.AtUtc,
            Median(settled.Select(s => s.MainSliderMs)), Median(settled.Select(s => s.AsioSliderMs)),
            last.MixedFloorMs, last.WasapiFloorMs, last.AsioFloorMs);
    }

    /// <summary>When the machine went to sleep, read from the history itself: the start of the most recent gap between
    /// samples of at least <paramref name="gapThreshold"/>, counting up to <paramref name="nowUtc"/>. Nothing is recorded
    /// while the machine sleeps, so the gap IS the sleep. Null when there is no such gap.</summary>
    public DateTime? SleepStartedBefore(DateTime nowUtc, TimeSpan gapThreshold)
    {
        var times = samples.Select(s => s.AtUtc).Where(t => t <= nowUtc).Order().Append(nowUtc).ToList();
        for (var i = times.Count - 1; i > 0; i--)
            if (times[i] - times[i - 1] >= gapThreshold) return times[i - 1];
        return null;
    }

    /// <summary>The upper median, so an even split between two values leans to the one with more cushion.</summary>
    private static int Median(IEnumerable<int> values)
    {
        var sorted = values.Order().ToArray();
        return sorted[sorted.Length / 2];
    }
}
