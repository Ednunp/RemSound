namespace RemSound.App;

internal static partial class SelfTest
{
    /// <summary>PRIORITY MODE ON WINDOWS 10 (2026-09-28). Priority mode asks Windows for full speed and for its timer request to
    /// be honoured in one call. The timer flag came with Windows 11; Windows 10 is reported to refuse the whole call as an
    /// invalid parameter, which left Windows 10 machines (and the service on them, which always runs in priority mode) without
    /// full speed too. Windows is stood in for: one that knows both flags takes one call; one that refuses the timer flag gets
    /// the full-speed flag on its own next - never a second call beside the first, since each call replaces the last; any
    /// other failure is logged and not retried; switching off hands both back in one call.</summary>
    private static string? PriorityModeOnWindows10()
    {
        const uint both = PerformanceMode.FullSpeedFlagForTest | PerformanceMode.TimerFlagForTest;
        var restore = PerformanceMode.SetThrottlingForTest;
        try
        {
            // What Priority mode asks for, on and off (not streaming), through the one call every flag goes by.
            var on = PerformanceMode.ThrottlingMaskFor(priorityMode: true, streaming: false);
            Check(on == both, $"Priority mode asks for full speed and the timer flag together - it asks for {on}");
            List<uint> Calls(Func<uint, (bool, int)> windows, bool disable, List<string> log)
            {
                var calls = new List<uint>();
                PerformanceMode.SetThrottlingForTest = mask => { calls.Add(mask); return windows(mask); };
                PerformanceMode.TrySetThrottlingMask(disable ? on : 0u, "priority mode", log.Add);
                return calls;
            }

            // Windows 11: both flags in one call.
            var log11 = new List<string>();
            var eleven = Calls(_ => (true, 0), disable: true, log11);
            Check(eleven.SequenceEqual([both]), $"Windows 11 takes both flags in one call - it was asked [{string.Join(", ", eleven)}]");
            Check(log11.Count == 0, "and nothing needs saying");

            // Windows 10: refuses the timer flag, so the whole call; then the full-speed flag on its own.
            var log10 = new List<string>();
            var ten = Calls(mask => (mask & PerformanceMode.TimerFlagForTest) != 0 ? (false, 87) : (true, 0), disable: true, log10);
            Check(ten.SequenceEqual([both, PerformanceMode.FullSpeedFlagForTest]),
                $"Windows 10 refuses the timer flag: it must be asked for full speed on its own next - it was asked [{string.Join(", ", ten)}]");
            Check(log10.Any(l => l.Contains("does not know the timer flag", StringComparison.Ordinal)), "and the log must say which Windows this was");

            // The fast timer alone while streaming, on that Windows: nothing else to ask for, and no failure to report.
            var timerLog = new List<string>();
            var timerCalls = new List<uint>();
            PerformanceMode.SetThrottlingForTest = mask => { timerCalls.Add(mask); return (mask & PerformanceMode.TimerFlagForTest) != 0 ? (false, 87) : (true, 0); };
            Check(PerformanceMode.TrySetThrottlingMask(PerformanceMode.TimerFlagForTest, "fast timer", timerLog.Add) && timerCalls.SequenceEqual([PerformanceMode.TimerFlagForTest, 0u]),
                $"the fast timer alone on Windows 10 settles for nothing else to set - it was asked [{string.Join(", ", timerCalls)}]");

            // Some other refusal: logged, not retried.
            var logOther = new List<string>();
            var other = Calls(_ => (false, 5), disable: true, logOther);
            Check(other.Count == 1 && logOther.Any(l => l.Contains("win32=5", StringComparison.Ordinal)), "any other refusal is logged once and not retried");

            // Switching off: both handed back to Windows in one call.
            var offLog = new List<string>();
            var off = Calls(_ => (true, 0), disable: false, offLog);
            Check(off.SequenceEqual([0u]), $"switching off hands both flags back in one call - it was [{string.Join(", ", off)}]");
        }
        finally { PerformanceMode.SetThrottlingForTest = restore; }
        return "Windows 11 takes both flags at once; a Windows 10 that refuses the timer flag gets full speed on its own; "
             + "other refusals are logged, not retried; off hands both back";
    }
}
