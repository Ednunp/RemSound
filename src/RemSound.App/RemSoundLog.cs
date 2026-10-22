using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// The log: what happened, one line each, in a text file written to <see cref="AppConfig.LogsDirectory"/>
/// (<c>user settings and logs\logs\</c> beside the exe, unless <c>--config-dir</c> moves it) - and, beside it, the readings
/// RemSound measures every second, in a CSV file of their own (2026-10-22, issue 35).
///
/// <para>Every line of the log starts with a severity word, the time and a category, tab-separated:
/// <c>WARN 14:02:31.250 Peer: LAPTOP disconnected - ...</c> (<see cref="LogLines"/>). The date is said when the log starts
/// and whenever it changes. The readings go through <see cref="ReadingsRecorder"/>: a minute row summing up every minute,
/// and every second only around a problem (<see cref="Capture"/>).</para>
///
/// <para>The files are made lazily, on the first line written while <see cref="Enabled"/> is true. With logs turned off
/// in Preferences (the default) nothing is made at all, so the App can construct this freely. Flipping
/// <see cref="Enabled"/> on mid-session is safe: the next line makes the file.</para>
/// </summary>
internal sealed class RemSoundLog : IDisposable
{
    private StreamWriter? writer;
    private bool fileCreationFailed;
    // Bytes written to the CURRENT file, tracked so a multi-day session with logging on can't
    // grow one file without bound (2026-07-26 resource audit: ~25-60 MB/day while streaming,
    // startup-only pruning never touches the live file). At the cap the file is closed with a
    // "continued in next file" line and a fresh file starts — the startup pruning
    // then ages out the closed ones like any other log. Internal seam so the self-test can
    // roll at a tiny size instead of writing 50 MB.
    private long bytesWrittenToCurrentFile;
    private int fileOrdinal;
    /// <summary>The name this session's first file settled on (LogFileNames): every later part carries it, with "-partN",
    /// and so does the readings file.</summary>
    private string? sessionStem;
    private DateTime? dateSaid;
    internal long RollAfterBytes { get; set; } = 50L * 1024 * 1024;
    /// <summary>How many times the log has rolled to a fresh file this session (test seam).</summary>
    internal int RollCount { get; private set; }
    /// <summary>Serialises all writes. StreamWriter is documented as non-thread-safe and
    /// concurrent WriteLine calls from the mix loop, heartbeat thread, network listener,
    /// UI thread and ASIO callback can interleave bytes into a single line in the output
    /// file. Worse, an interleaved write can leave the StreamWriter's internal char buffer
    /// in a state that throws on the next Flush — that exception escapes the inner try/catch
    /// and can bring the process down. A single gate around every WriteLine, every Dispose
    /// and every lazy-init step serialises writes cleanly; the cost is microseconds and
    /// worth the diagnostic integrity.</summary>
    private readonly object writeGate = new();

    /// <summary>Path of the log file once it has been created. Null until the first write
    /// arrives with <see cref="Enabled"/> true (or null forever if logging is never enabled
    /// or file creation fails).</summary>
    public string? Path { get; private set; }

    /// <summary>The readings file, once the first row has gone into it.</summary>
    public string? ReadingsPath { get; private set; }

    /// <summary>Master gate for all writes. When false, <see cref="Event"/>, <see cref="Readings"/> and
    /// <see cref="Capture"/> all short-circuit before touching the file system — no creation, no headers, no data.
    /// Defaults to false so the App can construct the log object before reading the user's preference; the App pushes the
    /// real value in after. Switching it off also forgets the readings waiting in memory, so none can reach the file
    /// later.</summary>
    public bool Enabled
    {
        get => enabled;
        set
        {
            lock (writeGate)
            {
                enabled = value;
                if (!value) readings?.Forget();
            }
        }
    }
    private bool enabled;

    /// <summary>The clock the log runs on - this computer's time. A test seam: the gate steps through an hour in a moment.</summary>
    internal Func<DateTime> NowForTest { get; set; } = () => DateTime.Now;

    /// <summary>Test tap: every <see cref="Capture"/>, before the switch, so the gate can prove the app keeps the seconds
    /// around a problem without a log file existing.</summary>
    internal Action<string>? CaptureTapForTest;

    /// <summary>The log the running window is writing, so the crash handlers can put the failure IN it, at the moment
    /// it happened, rather than only in a separate file beside it. Ed's 2026-09-20 error left no trace at all; a crash
    /// file nobody thinks to look for is only half a fix.</summary>
    public static RemSoundLog? Current { get; private set; }

    public RemSoundLog()
    {
        // No file work in the constructor. EnsureFileOpenLocked does it lazily on first
        // write, only when Enabled has been confirmed true.
        Current = this;
    }

    /// <summary>Open the log if it isn't open yet, starting it with a line saying what it is. Must be called holding
    /// <see cref="writeGate"/>. False if creation has failed (now or earlier): the caller gives up on its line.</summary>
    private bool EnsureFileOpenLocked()
    {
        if (writer is not null) return true;
        if (fileCreationFailed || disposed) return false;
        try
        {
            var dir = AppConfig.LogsDirectory;
            Directory.CreateDirectory(dir);
            fileOrdinal++;
            var (path, stream, stem) = LogFileNames.CreateNew(dir, sessionStem ?? LogFileNames.AppStem(DateTime.Now), fileOrdinal);
            sessionStem = stem;
            Path = path;
            writer = new StreamWriter(stream)
            {
                AutoFlush = true,
            };
            bytesWrittenToCurrentFile = 0;
            var now = NowForTest();
            var offset = TimeZoneInfo.Local.GetUtcOffset(now);
            dateSaid = now.Date;
            writer.WriteLine(LogLines.Format(LogLevel.Info, now, "Log",
                $"RemSound {typeof(RemSoundLog).Assembly.GetName().Version?.ToString(3)} log for {Environment.MachineName}, "
                + $"{(fileOrdinal == 1 ? "started" : "continued")} {now:dddd d MMMM yyyy}. Times are this computer's "
                + $"(UTC{(offset < TimeSpan.Zero ? "-" : "+")}{offset:hh\\:mm}). What it measures every second is in "
                + $"{stem}-readings.csv beside this file: a row for every minute, and every second around a problem."));
            return true;
        }
        catch
        {
            // Logging is best-effort. Locked dir, permissions, disk full — any of these
            // shouldn't kill the app. Park the file as failed so we don't keep retrying
            // creation on every subsequent write attempt.
            writer = null;
            Path = null;
            fileCreationFailed = true;
            return false;
        }
    }

    /// <summary>
    /// Test tap: every event passes through here BEFORE the Enabled gate, so the gate can prove that
    /// driving a control actually REPORTS what it did.
    ///
    /// <para>Ed, 2026-08-24: "we have spent months and months building logs on top of logs on top of
    /// logs for every function. but the thing is, we've never tested those logs." He is right. The
    /// control suite proved that moving a control changed the thing it governs and said nothing about
    /// whether the change was recorded. A log nobody has tested is a log you discover is empty on the
    /// night you need it — which is precisely the night there is no time to add one.</para>
    ///
    /// <para>Deliberately before the Enabled check. The question is whether the handler REPORTS what
    /// it did, which is a property of the call site, not of whether the user happens to have the log
    /// file switched on. Tapping here also means the gate never writes a real log file in order to
    /// test logging. Same lesson as TICK1: behaviour must not sit behind the log switch.</para>
    ///
    /// <para>It sees a line as it was asked for: a line that says its own level and category as "Category: text", every
    /// other one in its own words, as the suites have always matched them.</para>
    /// </summary>
    internal Action<string>? EventTapForTest;

    /// <summary>A line in its own words. Its level and category come from them (<see cref="LogLines.Classify"/>).</summary>
    public void Event(string message)
    {
        EventTapForTest?.Invoke(message);
        HeadlessRecords.Note(HeadlessRecords.Log, message);
        if (!Enabled) return;
        var (level, category, text) = LogLines.Classify(message);
        Write(level, category, text);
    }

    /// <summary>A line that says its own level and category: "WARN Peer: LAPTOP disconnected - ...".</summary>
    public void Event(LogLevel level, string category, string text)
    {
        var said = $"{category}: {text}";
        EventTapForTest?.Invoke(said);
        HeadlessRecords.Note(HeadlessRecords.Log, said);
        if (!Enabled) return;
        Write(level, category, text);
    }

    private void Write(LogLevel level, string category, string text)
    {
        lock (writeGate)
        {
            if (!enabled || !EnsureFileOpenLocked()) return;
            var now = NowForTest();
            if (dateSaid != now.Date)
            {
                dateSaid = now.Date;
                WriteLineLocked(LogLines.Format(LogLevel.Info, now, "Log", $"the date is now {now:dddd d MMMM yyyy}"));
            }
            WriteLineLocked(LogLines.Format(level, now, category, text));
        }
    }

    /// <summary>Must be called holding <see cref="writeGate"/>.</summary>
    private void WriteLineLocked(string line)
    {
        if (!EnsureFileOpenLocked()) return;
        try
        {
            writer!.WriteLine(line);
            AccountAndMaybeRollLocked(line.Length);
        }
        catch { /* swallow — log is best-effort */ }
    }

    // ---- THE READINGS FILE ------------------------------------------------------------------------------------------

    private ReadingsRecorder? readings;
    private StreamWriter? readingsWriter;
    private long readingsBytes;
    private int readingsPart;
    private bool readingsFailed;

    /// <summary>One second's readings: the machine-wide row and one for each stream on each output. They go to the
    /// readings file as <see cref="ReadingsRecorder"/> says.</summary>
    public void Readings(ReadingsRow machine, IReadOnlyList<ReadingsRow>? streams = null)
    {
        if (!Enabled) return;
        lock (writeGate)
        {
            if (!enabled) return;
            readings ??= new ReadingsRecorder(machine.Schema, WriteReadingLineLocked);
            try { readings.Second(NowForTest(), machine, streams); }
            catch { /* best-effort */ }
        }
    }

    /// <summary>Keep the seconds from <paramref name="lookBack"/> ago and the next 30: a problem a reader will want every
    /// second of. Returns words for the log line that reports it ("every second from 14:01:31 to 14:02:31 is in the
    /// readings file"), or null when nothing is being kept (logging off, or no readings yet).</summary>
    public string? Capture(string why, TimeSpan lookBack)
    {
        CaptureTapForTest?.Invoke(why);
        if (!Enabled) return null;
        lock (writeGate)
        {
            if (!enabled || readings is null) return null;
            var (from, to) = readings.Capture(NowForTest(), lookBack);
            return $"every second from {from:HH:mm:ss} to {to:HH:mm:ss} goes in the readings file";
        }
    }

    /// <summary>Write now what the readings file will hold, rather than five minutes from now: Write logs now, a crash,
    /// and RemSound closing (<paramref name="closing"/> also sums up the minute still open).</summary>
    public void FlushReadings(bool closing = false)
    {
        lock (writeGate)
        {
            if (!enabled) return;
            try { readings?.Flush(NowForTest(), closing); }
            catch { /* best-effort */ }
        }
    }

    private void WriteReadingLineLocked(string line)
    {
        if (readingsFailed) return;
        try
        {
            if (readingsWriter is null)
            {
                if (!EnsureFileOpenLocked()) { readingsFailed = true; return; }
                readingsPart++;
                var dir = System.IO.Path.GetDirectoryName(Path!)!;
                var name = $"{sessionStem}-readings{(readingsPart > 1 ? $"-part{readingsPart}" : "")}.csv";
                ReadingsPath = System.IO.Path.Combine(dir, name);
                readingsWriter = new StreamWriter(new FileStream(ReadingsPath, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
                readingsBytes = 0;
                if (readingsPart > 1 && !line.StartsWith("Time,", StringComparison.Ordinal)) readingsWriter.WriteLine(readings!.HeaderLine);
            }
            readingsWriter.WriteLine(line);
            readingsBytes += line.Length + 2;
            if (readingsBytes >= RollAfterBytes)
            {
                readingsWriter.Dispose();
                readingsWriter = null;
            }
        }
        catch { readingsFailed = true; }
    }

    /// <summary>Account a just-written line and roll to a fresh file at the size cap. Must be
    /// called holding <see cref="writeGate"/>. Rolling closes the current file with a pointer
    /// line and clears <see cref="writer"/>, so the next write lazily creates the successor —
    /// a failed re-open then degrades to the existing best-effort behaviour. The cap bounds a
    /// multi-day always-logging session to a chain of capped files (which the startup pruning
    /// ages out) instead of one unbounded giant.</summary>
    private void AccountAndMaybeRollLocked(int lineChars)
    {
        bytesWrittenToCurrentFile += lineChars + 2; // + newline
        if (bytesWrittenToCurrentFile < RollAfterBytes) return;
        try
        {
            writer?.WriteLine(LogLines.Format(LogLevel.Info, NowForTest(), "Log", "this file has reached its size limit - the log goes on in a fresh file"));
            writer?.Dispose();
        }
        catch { /* swallow */ }
        writer = null;
        RollCount++;
    }

    public void Dispose()
    {
        lock (writeGate)
        {
            try
            {
                if (enabled)
                {
                    readings?.Flush(NowForTest(), closeMinute: true);
                    if (writer is not null) writer.WriteLine(LogLines.Format(LogLevel.Info, NowForTest(), "Log", "log stopped"));
                }
                writer?.Dispose();
                readingsWriter?.Dispose();
            }
            catch { /* swallow */ }
            writer = null;
            readingsWriter = null;
            // Nothing written after this may start a fresh file.
            disposed = true;
        }
    }
    private bool disposed;
}
