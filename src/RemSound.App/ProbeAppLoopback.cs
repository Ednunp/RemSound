using System.Diagnostics;
using RemSound.Sender;

namespace RemSound.App;

/// <summary>Focused diagnostic probe for the process-loopback activation hang (per-app send). Run via
/// `RemSound.exe --probe-apploopback [pid]`. Traces the whole activation path (hr, callback delivery,
/// timing) for the given pid — or the FIRST process named in the REMSOUND_PROBE_APP env var, else self.
/// Prints to the console; not part of the gate. Temporary investigation aid.</summary>
internal static class ProbeAppLoopback
{
    public static int Run(string[] args)
    {
        var outPath = Path.Combine(Path.GetTempPath(), "remsound-apploopback-probe.txt");
        var sw0 = new StreamWriter(outPath, append: false) { AutoFlush = true };
        void Line(string m) => sw0.WriteLine(m);
        ProcessLoopbackCapture.Diagnostic = m => Line($"  [{DateTime.Now:HH:mm:ss.fff}] {m}");

        int pid;
        var explicitPid = args.FirstOrDefault(a => int.TryParse(a, out _));
        if (explicitPid is not null) pid = int.Parse(explicitPid);
        else
        {
            var name = Environment.GetEnvironmentVariable("REMSOUND_PROBE_APP");
            var proc = string.IsNullOrWhiteSpace(name)
                ? null
                : Process.GetProcessesByName(name).FirstOrDefault();
            pid = proc?.Id ?? Environment.ProcessId;
        }

        Line($"process-loopback probe: target pid={pid} "
            + $"({(pid == Environment.ProcessId ? "SELF (silent)" : SafeName(pid))}), IsSupported={ProcessLoopbackCapture.IsSupported}");

        var sw = Stopwatch.StartNew();
        long bytes = 0;
        Exception? stopError = null;
        var stopped = new ManualResetEventSlim(false);
        var capture = new ProcessLoopbackCapture(pid);
        capture.DataAvailable += (_, e) => Interlocked.Add(ref bytes, e.BytesRecorded);
        capture.RecordingStopped += (_, e) => { stopError = e.Exception; stopped.Set(); };

        Line("starting capture...");
        capture.StartRecording();

        // Give activation up to 5s to complete or fail, then observe ~2s of data flow.
        var settled = stopped.Wait(5000);
        var afterActivate = sw.ElapsedMilliseconds;
        if (!settled) Thread.Sleep(2000); // capture is live — let some audio flow
        var seen = Interlocked.Read(ref bytes);

        Line($"--- after {afterActivate}ms: stopped={settled}, bytesCaptured={seen}, stopError={stopError?.GetType().Name}: {stopError?.Message}");

        var disposeSw = Stopwatch.StartNew();
        capture.Dispose();
        disposeSw.Stop();
        Line($"dispose took {disposeSw.ElapsedMilliseconds}ms (~2000ms = capture thread was still stuck in activation)");
        Line(seen > 0 ? "RESULT: capture DELIVERED audio" : "RESULT: NO audio captured (activation failed)");
        sw0.Dispose();
        return 0;
    }

    private static string SafeName(int pid)
    {
        try { return Process.GetProcessById(pid).ProcessName; } catch { return "unknown"; }
    }
}
