using System.Diagnostics;
using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// THE FAST TIMER WHILE STREAMING (Ed, 2026-09-28): the latency lab's "keep the fast Windows timer while RemSound is
/// hidden", made normal behaviour. Windows 11 ignores the 1 ms timer RemSound's audio loops ask for while no window of the
/// process can be seen, so a tray-minimised RemSound's 10 ms mix tick slipped to the 15.6 ms system tick. Measured that
/// night on Ed's desktop: p99 15.3 ms late without the process flag, 1.9 ms with it.
///
/// <para>This step's process has no window anyone can see (the gate runs hidden), which is exactly the case. So it
/// measures the real thing: the mix tick's own wait, run for three seconds with the flag off, with the app's own
/// streaming rule setting it, and after streaming stops. The flag is the one Priority mode lever that really reaches
/// Windows here (PerformanceMode.RealTimerFlagForTest), scoped to the step: it is process-scoped and harmless, and handed
/// back when the step ends.</para>
/// </summary>
internal static partial class SelfTest
{
    /// <summary>How late the mix tick may wake, 99 times in 100, with the flag held (Ed's desktop measured 1.9 ms).</summary>
    private const double FastTimerP99LimitMs = 4;

    /// <summary>Without the flag a hidden process wakes on the coarse 15.6 ms tick; at least this late at p99 shows it.</summary>
    private const double CoarseTickP99Ms = 8;

    private static string? TheFastTimerHoldsWhileStreaming()
    {
        // 1. The app's rule: held while audio moves, sent or received, and for the same quiet hold-down as Priority mode.
        var now = DateTime.UtcNow;
        Check(MainForm.FastTimerShouldHold(now.AddSeconds(-1), now), "audio moved a second ago: the fast timer must be held");
        Check(MainForm.FastTimerShouldHold(now.AddSeconds(-20), now), "a quiet spell inside the hold-down must not let it go (no flapping between songs)");
        Check(!MainForm.FastTimerShouldHold(now - MainForm.StreamingHoldDown - TimeSpan.FromSeconds(1), now), "past the hold-down it must be let go");
        Check(!MainForm.FastTimerShouldHold(DateTime.MinValue, now), "and never held before any audio has moved");

        // 2. The flags, in one place: the timer's alone while streaming; Priority mode's two, the timer among them - which is
        // also what the send-only service holds while sending, since it switches Priority mode on then.
        var timer = PerformanceMode.TimerFlagForTest;
        Check(PerformanceMode.ThrottlingMaskFor(priorityMode: false, streaming: false) == 0u, "not streaming, no Priority mode: every flag handed back to Windows");
        Check(PerformanceMode.ThrottlingMaskFor(priorityMode: false, streaming: true) == timer, "streaming alone: the timer flag, and nothing else of Priority mode's");
        Check((PerformanceMode.ThrottlingMaskFor(priorityMode: true, streaming: false) & timer) != 0
              && PerformanceMode.ThrottlingMaskFor(true, true) == PerformanceMode.ThrottlingMaskFor(true, false),
            "Priority mode must hold the timer flag among its own, streaming or not");

        // 3. Priority mode switched off WHILE streaming keeps the timer; streaming stopping under Priority mode leaves
        // Priority mode's flags alone. A test run records what each change asked Windows for.
        var said = new List<string>();
        PerformanceMode.KeepFastTimerWhileStreaming(false);
        PerformanceMode.Apply(false);
        try
        {
            PerformanceMode.KeepFastTimerWhileStreaming(true, said.Add);
            Check(PerformanceMode.StreamingFastTimer && PerformanceMode.LastThrottlingMaskForTest == timer, "streaming starts: the timer flag is asked for");
            PerformanceMode.Apply(true, said.Add);
            Check(PerformanceMode.LastThrottlingMaskForTest == PerformanceMode.ThrottlingMaskFor(true, true), "Priority mode on: its flags");
            PerformanceMode.Apply(false, said.Add);
            Check(PerformanceMode.LastThrottlingMaskForTest == timer,
                $"Priority mode switched off while streaming must keep the timer flag, not hand everything back (asked for {PerformanceMode.LastThrottlingMaskForTest})");
            PerformanceMode.Apply(true, said.Add);
            PerformanceMode.KeepFastTimerWhileStreaming(false, said.Add);
            Check(PerformanceMode.LastThrottlingMaskForTest == PerformanceMode.ThrottlingMaskFor(true, false),
                "streaming stopping under Priority mode must leave Priority mode's flags as they are");
            PerformanceMode.Apply(false, said.Add);
            Check(PerformanceMode.LastThrottlingMaskForTest == 0u, "and both off hands everything back");
            Check(said.Any(l => l.StartsWith("fast timer: on while streaming", StringComparison.Ordinal))
                  && said.Any(l => l.StartsWith("fast timer: off, not streaming", StringComparison.Ordinal)),
                $"each change must be logged (said: {string.Join(" | ", said)})");
        }
        finally
        {
            PerformanceMode.KeepFastTimerWhileStreaming(false);
            PerformanceMode.Apply(false);
        }

        // 4. Windows 7 has no SetProcessInformation: the call must be caught and logged, and nothing else happen.
        said.Clear();
        using (PerformanceMode.RealTimerFlagForTest())
        {
            PerformanceMode.ApiMissingForTest = true;
            try
            {
                PerformanceMode.KeepFastTimerWhileStreaming(true, said.Add);
                PerformanceMode.KeepFastTimerWhileStreaming(false, said.Add);
            }
            finally { PerformanceMode.ApiMissingForTest = false; }
            Check(said.Count(l => l.Contains("EntryPointNotFoundException", StringComparison.Ordinal)) == 2
                  && !said.Any(l => l.StartsWith("fast timer: on while streaming", StringComparison.Ordinal)),
                $"on Windows 7 each change must be caught and said, and never claimed as done (said: {string.Join(" | ", said)})");
        }

        // 5. The real thing, measured, on this hidden process.
        said.Clear();
        using (PerformanceMode.RealTimerFlagForTest())
        {
            if (PerformanceMode.ReadThrottlingControlMaskForTest() is not { } before)
                return Skip("this Windows cannot say what its power-throttling flags are (Windows 7, or a refusal), so there is nothing to measure against");
            Check(before == 0u, $"premise: this test process must start with no throttling flags of its own (it holds {before})");
            MixTickLateness(1);   // a second for Windows to settle on how it treats a process just started
            var off = MixTickLateness(3);
            if (off.P99 < CoarseTickP99Ms)
                return Skip($"this Windows keeps the fine timer for a hidden process anyway (the tick woke {off}), so the flag has nothing to show here");

            PerformanceMode.KeepFastTimerWhileStreaming(MainForm.FastTimerShouldHold(DateTime.UtcNow, DateTime.UtcNow), said.Add);
            var on = MixTickLateness(3);
            Check(on.P99 < FastTimerP99LimitMs,
                $"THE FAST TIMER: while streaming, a hidden RemSound's 10 ms mix tick must keep time - p99 under {FastTimerP99LimitMs} ms late "
                + $"(it woke {on}; without the flag {off}; said: {string.Join(" | ", said)})");
            Check(PerformanceMode.ReadThrottlingControlMaskForTest() == timer,
                $"and Windows must hold the timer flag for the process ({PerformanceMode.ReadThrottlingControlMaskForTest()})");

            PerformanceMode.KeepFastTimerWhileStreaming(MainForm.FastTimerShouldHold(DateTime.UtcNow - MainForm.StreamingHoldDown - TimeSpan.FromSeconds(1), DateTime.UtcNow), said.Add);
            Check(PerformanceMode.ReadThrottlingControlMaskForTest() == 0u, "streaming stopped: the flag must be handed back to Windows");
            var after = MixTickLateness(3);
            Check(after.P99 >= CoarseTickP99Ms, $"and the tick must be back on the coarse timer, the flag really gone (it woke {after})");
            return $"the rule held while streaming and let go after the hold-down; the flags in one place; Priority mode off while streaming "
                 + $"kept the timer; Windows 7's missing call caught; measured on this hidden process - off {off}; streaming {on}; stopped {after}";
        }
    }

    /// <summary>How late the mix tick wakes: MixingEngine.MixLoop's own wait (MMCSS Pro Audio, the 1 ms timer, WaitOne of the
    /// time left, a tick every 10 ms), run on a thread of its own for <paramref name="seconds"/>.</summary>
    private static TickLateness MixTickLateness(double seconds)
    {
        var late = new List<double>();
        var thread = new Thread(() =>
        {
            using var boost = new WindowsAudioThreadBoost("Pro Audio");
            using var timerResolution = new SystemTimerResolution(1);
            var ticksPerFrame = Stopwatch.Frequency * RemSound.Sender.MixingEngine.MixTickMs / 1000;
            var start = Stopwatch.GetTimestamp();
            var end = start + (long)(seconds * Stopwatch.Frequency);
            var next = start + ticksPerFrame;
            using var never = new ManualResetEvent(false);
            while (Stopwatch.GetTimestamp() < end)
            {
                var now = Stopwatch.GetTimestamp();
                if (next > now)
                {
                    never.WaitOne((int)Math.Clamp((next - now) * 1000 / Stopwatch.Frequency, 1, 50));
                    continue;
                }
                late.Add((now - next) * 1000.0 / Stopwatch.Frequency);
                if (now - next > ticksPerFrame * 4) next = now;
                next += ticksPerFrame;
            }
        }) { IsBackground = true, Name = "RemSound.FastTimerProbe" };
        thread.Start();
        thread.Join();
        var sorted = late.OrderBy(x => x).ToArray();
        double P(double q) => sorted.Length == 0 ? double.NaN : sorted[Math.Min(sorted.Length - 1, (int)(q * sorted.Length))];
        return new TickLateness(sorted.Length, P(0.5), P(0.99), sorted.LastOrDefault());
    }

    private sealed record TickLateness(int Ticks, double P50, double P99, double Max)
    {
        public override string ToString() => $"{Ticks} ticks, p50 {P50:0.0} ms, p99 {P99:0.0}, max {Max:0.0}";
    }
}
