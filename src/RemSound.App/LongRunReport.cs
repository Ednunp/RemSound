using System.Text;
using RemSound.Core;
using RemSound.Receiver;

namespace RemSound.App;

/// <summary>
/// One line, every few minutes, designed to answer ONE question: is anything creeping?
///
/// <para><b>Why it exists.</b> Ed has reported the WASAPI side going slow overnight three times, and
/// three times I have read the logs afterwards, found a real but different fault, fixed it, and not
/// fixed his problem. The reason is in the logs themselves. The per-second diagnostic line is enormous
/// — fifty megabytes in fourteen hours — and it is written only while a RECEIVER is running, so on his
/// send-only machine on 2026-09-08 it produced five lines in fourteen hours and then stopped. A whole
/// night of evidence that could not answer the question it was collected for.</para>
///
/// <para>So this line is the opposite of that one. Small, so a night of it is a few hundred lines you
/// can read end to end. Always written whenever logging is on, whether the machine is sending,
/// receiving, both or idle. And carrying only the values that could plausibly CREEP, so that reading
/// it is a matter of looking down a column rather than interpreting.</para>
///
/// <para><b>Why the WASAPI stage gets the most room.</b> Ed, 2026-09-09: the slowdown happens on
/// WASAPI and never on ASIO. The two lanes differ in exactly one place — the WASAPI output stage, with
/// its device buffer, its cushion target sized from the card's pull, and its rate-trimming resampler.
/// ASIO pulls straight from the engine and has none of that. A fault on one lane and not the other has
/// to live where they differ, so those numbers get named individually rather than summarised.</para>
///
/// <para><b>And it covers the send side too</b>, because Andre's version of this report was a slow
/// climb with no hibernation involved at all — so "it only happens on resume" is not established, and
/// an instrument that only fires on resume would beg the question.</para>
/// </summary>
internal static class LongRunReport
{
    /// <summary>How often the report is written. Small enough that a creep is visible within an hour,
    /// large enough that a twelve-hour night is under 150 lines.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// After a wake, the report switches to this interval for <see cref="BurstLength"/>.
    ///
    /// <para>Five minutes is right for spotting a creep and useless for a wake. On 2026-09-10 the whole
    /// post-wake episode — the false raise to 65, the reopen, the raise to 80 and the walk back to
    /// baseline — was over in about ninety seconds, and would have landed inside one five-minute line.
    /// The overnight wake on 2026-09-09 was the opposite: minutes of genuine underruns on BOTH lanes,
    /// which no one line could separate into network trouble versus render trouble.</para>
    /// </summary>
    public static readonly TimeSpan BurstInterval = TimeSpan.FromSeconds(10);

    /// <summary>How long the post-wake burst lasts. Long enough to cover the minutes of underruns seen
    /// after an overnight hibernate, short enough that a burst is still a handful of lines.</summary>
    public static readonly TimeSpan BurstLength = TimeSpan.FromMinutes(3);

    /// <summary>Everything the line reports, gathered by the caller in one pass so the numbers all
    /// belong to the same instant.</summary>
    internal readonly record struct Reading(
        TimeSpan Uptime,
        string Tag,
        AudioConfiguration Configuration,
        int WasapiBufferMs, int WasapiTargetMs, double WasapiUnderrunsPerSec, int WasapiLearnedFloorMs, double? WasapiRenderMsPerSec,
        int AsioBufferMs, int AsioTargetMs, double AsioUnderrunsPerSec, int AsioLearnedFloorMs, double? AsioRenderMsPerSec,
        IReadOnlyList<WasapiOutputStage> Stages,
        long SendFramesPerSec,
        string SourceDrift,
        // The worst network arrival gap and the worst render-callback gap per lane in this window.
        // Split on purpose: after the overnight wake on 2026-09-09 both lanes underran for minutes,
        // and a single underrun count cannot say whether packets were arriving late or the devices
        // were rendering late. Null when the per-second line did not run, never a fake zero.
        int? NetGapMaxMs = null,
        int? WasapiRenderGapMaxMs = null,
        int? AsioRenderGapMaxMs = null);

    /// <summary>
    /// Format the line. Pure, so the gate can assert what it says without a machine that has been
    /// running for hours.
    ///
    /// <para>Deliberately one line per report even though it is long: a creep is found by comparing
    /// the same position across many reports, and that only works if every report has the same shape.
    /// Values that are absent read as a dash rather than a zero, because "no ASIO lane" and "an ASIO
    /// lane holding zero" are different facts and a zero would hide the difference.</para>
    /// </summary>
    public static string Format(in Reading r)
    {
        var sb = new StringBuilder();
        sb.Append("longrun ").Append(FormatUptime(r.Uptime));
        if (!string.IsNullOrEmpty(r.Tag)) sb.Append(" [").Append(r.Tag).Append(']');
        sb.Append(" [").Append(r.Configuration.Describe()).Append(']');

        sb.Append(" | wasapi jit=").Append(r.WasapiBufferMs).Append('/').Append(r.WasapiTargetMs).Append("ms")
          .Append(" under=").Append(r.WasapiUnderrunsPerSec.ToString("0.00")).Append("/s")
          .Append(" floor=").Append(r.WasapiLearnedFloorMs).Append("ms")
          .Append(" render=").Append(RenderMs(r.WasapiRenderMsPerSec));

        sb.Append(" | asio jit=").Append(r.AsioBufferMs).Append('/').Append(r.AsioTargetMs).Append("ms")
          .Append(" under=").Append(r.AsioUnderrunsPerSec.ToString("0.00")).Append("/s")
          .Append(" floor=").Append(r.AsioLearnedFloorMs).Append("ms")
          .Append(" render=").Append(RenderMs(r.AsioRenderMsPerSec));

        // NETWORK VERSUS RENDER, split per lane. See Reading for why one underrun count cannot tell the
        // difference between packets arriving late and a device rendering late.
        sb.Append(" | gaps net=").Append(GapMs(r.NetGapMaxMs))
          .Append(" wasapiRender=").Append(GapMs(r.WasapiRenderGapMaxMs))
          .Append(" asioRender=").Append(GapMs(r.AsioRenderGapMaxMs));
        static string GapMs(int? v) => v is { } x ? x + "ms" : "-";

        // THE WASAPI-ONLY STAGE. Named per device, never averaged: two cards with different pull sizes
        // hold different targets on purpose, and an average of the two would hide one of them climbing.
        sb.Append(" | stage=");
        if (r.Stages.Count == 0)
        {
            sb.Append("none");
        }
        else
        {
            for (var i = 0; i < r.Stages.Count; i++)
            {
                if (i > 0) sb.Append("; ");
                sb.Append(r.Stages[i]);
            }
        }

        sb.Append(" | send frames=").Append(r.SendFramesPerSec).Append("/s");
        sb.Append(" srcDrift=").Append(string.IsNullOrEmpty(r.SourceDrift) ? "[]" : r.SourceDrift);
        return sb.ToString();
    }

    /// <summary>
    /// Render cost, or a dash when it was not measured this window.
    ///
    /// <para>It is NOT read from the counter directly. That counter resets as it is read, and the
    /// per-second diagnostic line already owns it — a second reader would take half the samples and
    /// starve the first. That is not hypothetical: on 2026-09-06 exactly this double-read starved the
    /// auto-tune's per-lane windows, a regression I introduced and then had to find. So this value is
    /// accumulated from the per-second line's own reading and is simply absent on a tick where that
    /// line did not run, which a dash says plainly and a zero would not.</para>
    /// </summary>
    private static string RenderMs(double? value)
        => value is { } v ? v.ToString("0.0") + "ms/s" : "-";

    /// <summary>Hours and minutes since the session started, zero-padded so the column lines up when
    /// a night of these is read end to end.</summary>
    private static string FormatUptime(TimeSpan t)
        => $"t=+{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
}
