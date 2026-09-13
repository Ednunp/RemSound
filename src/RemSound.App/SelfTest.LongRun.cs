using RemSound.Core;
using RemSound.Receiver;

namespace RemSound.App;

internal static partial class SelfTest
{
    /// <summary>
    /// THE LONG-RUN LINE MUST BE WRITTEN WHEN THE OLD ONE WASN'T, AND MUST CARRY THE WASAPI-ONLY STAGE.
    ///
    /// <para>Ed has reported the WASAPI side going slow after a night three times. Three times the logs
    /// could not answer it. The reason is not subtle: the per-second diagnostic line is gated on there
    /// being buffer samples or render reads, so on his send-only machine on 2026-09-08 it wrote five
    /// lines in fourteen hours and then stopped. A whole night of evidence, absent.</para>
    ///
    /// <para>So the thing to pin is not the formatting. It is that the line survives the states where
    /// the old one went quiet, and that it carries the numbers that can actually creep — above all the
    /// WASAPI output stage, which is the one thing the ASIO lane does not have and therefore the only
    /// place a fault seen on WASAPI and never on ASIO can live.</para>
    ///
    /// <para>Not per-configuration in the usual sense: the line is written in ALL of them, and the test
    /// checks exactly that. What it must NOT do is report an ASIO-only stage figure, because there
    /// isn't one — that absence is the finding, not a gap.</para>
    /// </summary>
    private static string? AuditLongRunReportSaysWhatCreeps()
    {
        // Every audio configuration must produce a line. A report that goes quiet in one of them is
        // how the last three nights were wasted.
        var seen = new List<string>();
        foreach (var configuration in AudioConfigurations.All)
        {
            var stages = configuration.UsesWasapi()
                ? new[] { new WasapiOutputStage("Card", 12, 12, 10, 3, -1) }
                : Array.Empty<WasapiOutputStage>();
            var line = LongRunReport.Format(new LongRunReport.Reading(
                Uptime: TimeSpan.FromHours(9) + TimeSpan.FromMinutes(35),
                Tag: "",
                Configuration: configuration,
                WasapiBufferMs: 31, WasapiTargetMs: 36, WasapiUnderrunsPerSec: 1.2, WasapiLearnedFloorMs: 35,
                WasapiRenderMsPerSec: 22.4,
                AsioBufferMs: 28, AsioTargetMs: 30, AsioUnderrunsPerSec: 0.0, AsioLearnedFloorMs: 26,
                AsioRenderMsPerSec: 26.1,
                Stages: stages,
                SendFramesPerSec: 400,
                SourceDrift: "[]"));

            Check(line.StartsWith("longrun ", StringComparison.Ordinal),
                $"{configuration.Describe()}: every report must be findable by one grep (got \"{line}\")");
            Check(line.Contains(configuration.Describe(), StringComparison.Ordinal),
                $"{configuration.Describe()}: the line must say which configuration it was taken in — the same numbers "
                + "mean different things in each, and a log that cannot say which cannot answer the question");
            Check(line.Contains("t=+09:35", StringComparison.Ordinal),
                $"{configuration.Describe()}: the line must carry time-since-start, because the whole point is comparing "
                + $"a value against itself hours earlier (got \"{line}\")");
            seen.Add(configuration.Describe());
        }

        // THE WASAPI-ONLY STAGE, named per device. This is the part that makes the line worth writing:
        // buffer, target, the card's own pull, the measured clock and the correction being applied.
        var wasapiLine = LongRunReport.Format(NewReading(
            new[] { new WasapiOutputStage("Roger On", 19, 12, 10, 458, 533) }));
        Check(wasapiLine.Contains("Roger On", StringComparison.Ordinal),
            "the stage must be named per DEVICE — two cards hold different targets on purpose, and an average would "
            + "hide one of them climbing");
        Check(wasapiLine.Contains("dev=19/12ms", StringComparison.Ordinal),
            $"the stage must report its cushion AGAINST its target, not either alone (got \"{wasapiLine}\")");
        Check(wasapiLine.Contains("gulp=10ms", StringComparison.Ordinal),
            "the card's pull size must be there — it is what the target is sized from, so if the target climbs it says "
            + "whether the card changed or the sizing did");
        Check(wasapiLine.Contains("clock=+458ppm", StringComparison.Ordinal) && wasapiLine.Contains("corr=+533ppm", StringComparison.Ordinal),
            $"the measured clock and the correction must both appear (got \"{wasapiLine}\") — a correction pinned at its "
            + "cap while the clock wanders is the signature of a stage fighting something it cannot win");

        // ASIO HAS NO SUCH STAGE, and the line must say so rather than printing a misleading zero.
        var asioOnly = LongRunReport.Format(NewReading(Array.Empty<WasapiOutputStage>()));
        Check(asioOnly.Contains("stage=none", StringComparison.Ordinal),
            $"with no WASAPI output the line must say the stage is absent, not report zeros (got \"{asioOnly}\") — the "
            + "absence is the finding: it is the one thing WASAPI has that ASIO does not");

        // A VALUE THAT WAS NOT MEASURED MUST NOT READ AS ZERO. The render figures come from a counter
        // that resets as it is read and is owned by the per-second line, so on a tick where that line
        // did not run there is genuinely no number — and "0.0ms/s" would look like an idle lane.
        var unmeasured = LongRunReport.Format(NewReading(Array.Empty<WasapiOutputStage>(), render: null));
        Check(unmeasured.Contains("render=-", StringComparison.Ordinal),
            $"an unmeasured render figure must read as a dash, never as zero (got \"{unmeasured}\") — zero means an idle "
            + "lane, absent means nobody looked, and confusing the two is how a quiet log gets misread as a healthy one");

        // A RESUME MUST BE MARKED. Otherwise the report either side of a wake is indistinguishable from
        // any other, and comparing before-and-after means counting timestamps by hand.
        var tagged = LongRunReport.Format(NewReading(Array.Empty<WasapiOutputStage>(), tag: "after-resume"));
        Check(tagged.Contains("[after-resume]", StringComparison.Ordinal),
            $"a report taken after a wake must say so (got \"{tagged}\")");

        // And the interval has to stay small enough to see a creep and large enough to read a night of.
        Check(LongRunReport.Interval <= TimeSpan.FromMinutes(10) && LongRunReport.Interval >= TimeSpan.FromMinutes(1),
            $"the report interval must stay between 1 and 10 minutes (got {LongRunReport.Interval}) — longer and an "
            + "overnight creep hides inside one window, shorter and a night of it is too long to read");

        return $"one line per {LongRunReport.Interval.TotalMinutes:0} minutes in all three configurations ({string.Join(", ", seen)}), "
             + "naming the WASAPI-only stage per device, saying \"none\" where ASIO has no such stage, and a dash rather "
             + "than a zero for anything not measured";
    }

    /// <summary>
    /// AND IT MUST ACTUALLY BE WRITTEN, IN THE STATE THAT SILENCED THE OLD LINE.
    ///
    /// <para>Everything above tests the formatting, and formatting was never the problem. The per-second
    /// diagnostic line formats beautifully; it just wasn't written. On 2026-09-08 Ed's machine produced
    /// five diagnostic lines in fourteen hours because that line sits behind a gate that wants buffer
    /// samples or render reads, and a machine that is only sending has neither.</para>
    ///
    /// <para>So this drives the REAL per-second tick on a real form with nothing running at all — no
    /// receiver, no sender, no audio — and requires a line to come out. That is the exact state the old
    /// one goes quiet in. Writing the report and testing only its text would have reproduced the
    /// original mistake in a new file: proving the sentence is well-formed says nothing about whether
    /// anybody ever says it.</para>
    /// </summary>
    private static string? AuditLongRunReportIsActuallyWritten()
    {
        MainForm form;
        try { form = new MainForm(null, Profile.NewBlank(), null, null, headless: true); }
        catch (Exception ex) { return Skip($"headless main window could not be built: {ex.GetType().Name}: {ex.Message}"); }

        using (form)
        {
            var captured = new List<string>();
            form.LogForTest.EventTapForTest = line => { lock (captured) captured.Add(line); };
            try
            {
                // IDLE. Not sending, not receiving, no device open — the state Ed's machine spent
                // fourteen hours in while writing almost nothing.
                form.SnapshotTickForTest();
                var first = captured.FirstOrDefault(l => l.StartsWith("longrun ", StringComparison.Ordinal));
                first = Require(first,
                    "the very first tick must write a long-run line even with nothing running — this is the exact state "
                    + "in which the per-second diagnostic line goes silent, and the whole reason this report exists");

                Check(first.Contains("stage=", StringComparison.Ordinal),
                    $"the line must carry the WASAPI stage field even when there is no stage yet (got \"{first}\")");
                Check(first.Contains("render=-", StringComparison.Ordinal),
                    $"with no render having happened, the render figure must read as absent rather than as a measured "
                    + $"zero (got \"{first}\")");

                // AND IT MUST NOT REPEAT EVERY SECOND. A per-second version of this would be the very
                // thing it replaces — fifty megabytes that nobody can read end to end.
                captured.Clear();
                form.SnapshotTickForTest();
                form.SnapshotTickForTest();
                var again = captured.Count(l => l.StartsWith("longrun ", StringComparison.Ordinal));
                Check(again == 0,
                    $"the report must not fire on every tick ({again} extra lines in two ticks) — at one a second a night "
                    + "of these is as unreadable as the line it replaces");

                return "the real per-second tick writes a long-run line while idle — no receiver, no sender, no device — "
                     + "and then holds off until its interval is up";
            }
            finally { form.LogForTest.EventTapForTest = null; }
        }
    }

    private static LongRunReport.Reading NewReading(
        IReadOnlyList<WasapiOutputStage> stages, double? render = 22.4, string tag = "")
        => new(
            Uptime: TimeSpan.FromHours(9) + TimeSpan.FromMinutes(35),
            Tag: tag,
            Configuration: AudioConfiguration.WasapiOnly,
            WasapiBufferMs: 31, WasapiTargetMs: 36, WasapiUnderrunsPerSec: 1.2, WasapiLearnedFloorMs: 35,
            WasapiRenderMsPerSec: render,
            AsioBufferMs: 28, AsioTargetMs: 30, AsioUnderrunsPerSec: 0.0, AsioLearnedFloorMs: 26,
            AsioRenderMsPerSec: render,
            Stages: stages,
            SendFramesPerSec: 400,
            SourceDrift: "[]");
}
