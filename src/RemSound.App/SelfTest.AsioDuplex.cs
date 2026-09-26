using NAudio.Wave;
using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// ONE ASIO DRIVER INSTANCE CARRIES BOTH DIRECTIONS - on a real driver, when one is named.
///
/// <para>The gate never opens an ASIO driver on its own: headless boxes have none, and Ed's Audient has
/// crashed natively on close. So this step runs only when <c>REMSOUND_ASIO_TEST_DRIVER</c> names an
/// installed driver, and skips loudly otherwise. What it proves is the fix for "you cannot send and
/// receive on the same ASIO device" (Anthony Reyers, 2026-09-04): two holders - the shape of a capture
/// backend and a render backend - share ONE instance, it runs full duplex, and it closes when the last
/// of them lets go.</para>
/// </summary>
internal static partial class SelfTest
{
    private static string? AsioOneDriverBothDirections()
    {
        var driver = Environment.GetEnvironmentVariable("REMSOUND_ASIO_TEST_DRIVER");
        if (string.IsNullOrWhiteSpace(driver))
        {
            return Skip("set REMSOUND_ASIO_TEST_DRIVER to the name of an installed ASIO driver to run this on real hardware");
        }
        // Never open a driver another RemSound on this desktop may be holding: a second open of one ASIO driver from two
        // processes is the vendor-driver crash this whole shared-device design exists to avoid (found 2026-09-24).
        var mine = System.Diagnostics.Process.GetCurrentProcess();
        var others = System.Diagnostics.Process.GetProcessesByName("RemSound").Where(p => p.Id != mine.Id && p.SessionId == mine.SessionId).ToList();
        if (others.Count > 0)
            return Skip($"another RemSound is running on this desktop (process {others[0].Id}) and may hold {driver}; close it to run this on real hardware");

        var lines = new List<string>();
        void Log(string line) { lock (lines) lines.Add(line); }
        string Tail() { lock (lines) return string.Join(" | ", lines.TakeLast(5)); }

        var capture = SharedAsioDevice.Acquire(driver, Log);
        var playback = SharedAsioDevice.Acquire(driver, Log);
        var callbacks = 0;
        var reads = 0;
        var inputs = 0;
        var outputs = 0;
        try
        {
            Check(ReferenceEquals(capture, playback), "two holders of one driver name must share ONE device - that is the whole fix");

            // Open through the capture side first, exactly the order that failed on the Zoom in reverse
            // (playback held it, capture could not open a second instance).
            try { capture.EnsureOpen(); }
            catch (Exception ex) { Check(false, $"the driver would not open at all: {SharedAsioDevice.Explain(ex)} (log: {Tail()})"); return null; }
            Check(capture.IsOpen && SharedAsioDevice.IsOpenFor(driver), "after EnsureOpen the device must report open");
            inputs = capture.InputChannelCount;
            outputs = capture.OutputChannelCount;
            Check(inputs > 0 || outputs > 0, $"the driver must report channels ({inputs} in, {outputs} out)");

            if (capture.InputChannelCount > 0) capture.AttachCapture((_, _) => Interlocked.Increment(ref callbacks));
            if (playback.OutputChannelCount > 0)
            {
                playback.AttachPlayback(new CountingSilence(
                    WaveFormat.CreateIeeeFloatWaveFormat(SharedAsioDevice.SampleRate, playback.OutputChannelCount),
                    () => Interlocked.Increment(ref reads)));
            }
            Check(WaitUntil(() => Volatile.Read(ref callbacks) > 10 || Volatile.Read(ref reads) > 10, 3000),
                $"the driver must be running and calling back on the one instance ({callbacks} input callbacks, {reads} output reads; "
              + $"log: {Tail()})");
            Check(capture.FramesPerBuffer > 0, $"the driver must report its buffer size ({capture.FramesPerBuffer} frames)");

            // The second side letting go must NOT close the driver under the first.
            playback.DetachPlayback();
            playback.Release(Log, 30_000);
            playback = null!;
            Check(capture.IsOpen, "one holder letting go must leave the driver open for the other");
            var before = Volatile.Read(ref callbacks) + Volatile.Read(ref reads);
            Check(WaitUntil(() => Volatile.Read(ref callbacks) + Volatile.Read(ref reads) > before + 5, 2000),
                "...and still running for it");
        }
        finally
        {
            capture.DetachCapture();
            capture.Release(Log, 30_000);
            playback?.Release(Log, 30_000);
        }
        Check(!SharedAsioDevice.IsOpenFor(driver), $"after the last holder lets go the driver must be closed (log: {Tail()})");

        return $"\"{driver}\": one instance, {inputs} in / {outputs} out, full duplex "
             + $"({callbacks} input callbacks, {reads} output reads), survived one side leaving, closed with the last";
    }

    /// <summary>Silence for the outputs, counting how often the driver asked for it.</summary>
    private sealed class CountingSilence(WaveFormat format, Action onRead) : IWaveProvider
    {
        public WaveFormat WaveFormat { get; } = format;

        public int Read(byte[] buffer, int offset, int count)
        {
            Array.Clear(buffer, offset, count);
            onRead();
            return count;
        }
    }
}
