namespace RemSound.App;

/// <summary>
/// Lightweight tab-separated log file written to <see cref="RemSound.Core.AppConfig.LogsDirectory"/>
/// (<c>user settings and logs\logs\</c> beside the exe, unless <c>--config-dir</c> moves it).
/// Two row kinds:
///   SNAP  — periodic snapshot of runtime counters (made every second; see <see cref="Detail"/> for when it is written)
///   EVT   — one-off events (connect, disconnect, codec change, errors)
/// Format (the file opens with a header row naming these columns, starting with Kind):
///   SNAP\t{Timestamp}\t{Machine}\t{Connected}\t{SendRunning}\t{ReceiveRunning}\t{Codec}\t
///         {MaxLatencyMs}\t{TargetLatencyMs}\t{BufferMs}\t{SenderPackets}\t{SenderKB}\t
///         {SenderDevice}\t{ReceiverPackets}\t{ReceiverKB}\t{Underruns}\t{Drops}\t{ReceiveDevice}\t
///         {Heartbeat}\t{OpusFecRecoveries}\t{OpusUnrecoveredGaps}\t{MaxLatencyMsAsio}\t{TargetLatencyMsAsio}
///   EVT\t{Timestamp}\t{Message}
///
/// The log file is created lazily on the first write that arrives while <see cref="Enabled"/>
/// is true. When the user has logs turned off in Preferences (the default), no file is created
/// at all — the App can construct the log object freely without spawning empty files in
/// <c>logs\</c>. Flipping <see cref="Enabled"/> back on mid-session is also safe: the next
/// write creates the file with its header and the session continues normally.
/// </summary>
internal sealed class RemSoundLog : IDisposable
{
    // MaxLatencyMsAsio / TargetLatencyMsAsio columns hold the per-lane numbers in
    // BothIndependent mode. In WasapiOnly they are 0 and the legacy MaxLatencyMs /
    // TargetLatencyMs columns continue to mean "the receiver's only latency".
    private const string SnapHeader =
        "Kind\tTimestamp\tMachine\tConnected\tSendRunning\tReceiveRunning\tCodec\t" +
        "MaxLatencyMs\tTargetLatencyMs\tBufferMs\tSenderPackets\tSenderKB\t" +
        "SenderDevice\tReceiverPackets\tReceiverKB\tUnderruns\tDrops\tReceiveDevice\tHeartbeat\t" +
        "OpusFecRecoveries\tOpusUnrecoveredGaps\tMaxLatencyMsAsio\tTargetLatencyMsAsio";

    private StreamWriter? writer;
    private bool fileCreationFailed;
    // Bytes written to the CURRENT file, tracked so a multi-day session with logging on can't
    // grow one file without bound (2026-07-26 resource audit: ~25-60 MB/day while streaming,
    // startup-only pruning never touches the live file). At the cap the file is closed with a
    // "continued in next file" line and a fresh timestamped file starts — the startup pruning
    // then ages out the closed ones like any other log. Internal seam so the self-test can
    // roll at a tiny size instead of writing 50 MB.
    private long bytesWrittenToCurrentFile;
    private int fileOrdinal;
    /// <summary>The name this session's first file settled on (LogFileNames): every later part carries it, with "-partN".</summary>
    private string? sessionStem;
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

    /// <summary>Master gate for all writes. When false, <see cref="Event"/>, <see cref="Snapshot"/>,
    /// <see cref="Detail"/> and <see cref="Trouble"/> all short-circuit before touching the file system — no
    /// creation, no headers, no data. Defaults to false so the App can construct the log object
    /// before reading the user's preference; the App pushes the real value in after. Switching it off also
    /// forgets the per-second lines waiting in memory, so none of them can reach the file later.</summary>
    public bool Enabled
    {
        get => enabled;
        set
        {
            lock (writeGate)
            {
                enabled = value;
                if (!value) detailRule.Forget();
            }
        }
    }
    private bool enabled;

    /// <summary>When a line made every second is written: one second's worth a minute, and every second around a
    /// <see cref="Trouble"/>. See <see cref="RemSound.Core.LogDetailRule"/> for why (2026-10-19, issue 35).</summary>
    private readonly RemSound.Core.LogDetailRule detailRule = new();

    /// <summary>The clock the per-second rules run on. A test seam: the gate steps through an hour in a moment.</summary>
    internal Func<DateTime> UtcNowForTest { get; set; } = () => DateTime.UtcNow;
    /// <summary>Test tap: every <see cref="Trouble"/> call, before the switch, so the gate can prove the app reports a
    /// problem without a log file existing.</summary>
    internal Action<string>? TroubleTapForTest;

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

    /// <summary>Open the underlying file if it hasn't been opened yet and write the schema
    /// header + a "log started" event line. Must be called while holding
    /// <see cref="writeGate"/>. Returns true on success or if the file is already open;
    /// false if creation has failed (either now or earlier) — caller should give up on the
    /// current write.</summary>
    private bool EnsureFileOpenLocked()
    {
        if (writer is not null) return true;
        if (fileCreationFailed) return false;
        try
        {
            var dir = RemSound.Core.AppConfig.LogsDirectory;
            Directory.CreateDirectory(dir);
            fileOrdinal++;
            var (path, stream, stem) = RemSound.Core.LogFileNames.CreateNew(dir, sessionStem ?? RemSound.Core.LogFileNames.AppStem(DateTime.Now), fileOrdinal);
            sessionStem = stem;
            Path = path;
            writer = new StreamWriter(stream)
            {
                AutoFlush = true,
            };
            bytesWrittenToCurrentFile = 0;
            writer.WriteLine(SnapHeader);
            writer.WriteLine($"EVT\t{DateTime.Now:o}\tlog started");
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

    public void Snapshot(
        bool connected,
        bool sendRunning,
        bool receiveRunning,
        string codec,
        int maxLatencyMs,
        int targetLatencyMs,
        int bufferMs,
        long senderPackets,
        long senderBytes,
        string senderDevice,
        long receiverPackets,
        long receiverBytes,
        long underruns,
        long drops,
        string receiveDevice,
        string heartbeat,
        long opusFecRecoveries,
        long opusUnrecoveredGaps,
        int maxLatencyMsAsio = 0,
        int targetLatencyMsAsio = 0)
    {
        if (!Enabled) return;
        lock (writeGate)
        {
            if (!enabled) return;
            try
            {
                var line = string.Join('\t',
                    "SNAP",
                    DateTime.Now.ToString("o"),
                    Environment.MachineName,
                    connected,
                    sendRunning,
                    receiveRunning,
                    codec,
                    maxLatencyMs,
                    targetLatencyMs,
                    bufferMs,
                    senderPackets,
                    senderBytes / 1024,
                    Sanitize(senderDevice),
                    receiverPackets,
                    receiverBytes / 1024,
                    underruns,
                    drops,
                    Sanitize(receiveDevice),
                    Sanitize(heartbeat),
                    opusFecRecoveries,
                    opusUnrecoveredGaps,
                    maxLatencyMsAsio,
                    targetLatencyMsAsio);
                DetailLocked(line);
            }
            catch { /* swallow — log is best-effort */ }
        }
    }

    /// <summary>One of the per-second lines (the diagnostic line, the "streams" line, the delay estimate): written as
    /// <see cref="RemSound.Core.LogDetailRule"/> says - one second's worth a minute, and every second around a
    /// <see cref="Trouble"/>. The test tap and the headless record see every one, as they see every event.</summary>
    public void Detail(string message)
    {
        EventTapForTest?.Invoke(message);
        HeadlessRecords.Note(HeadlessRecords.Log, message);
        if (!Enabled) return;
        lock (writeGate)
        {
            if (!enabled) return;
            DetailLocked($"EVT\t{DateTime.Now:o}\t{Sanitize(message)}");
        }
    }

    /// <summary>Must be called holding <see cref="writeGate"/>. The line keeps the time it was made, so one written
    /// later from memory still says when it happened.</summary>
    private void DetailLocked(string line)
    {
        if (detailRule.Line(UtcNowForTest(), line)) WriteLineLocked(line);
    }

    /// <summary>
    /// Something went wrong that a reader will want every second of: an audible gap, a peer that stopped answering, the
    /// person pressing Write logs now. Writes the per-second lines from the last 30 seconds, then the next 30 as they
    /// come. At most once every ten minutes unless <paramref name="evenInsideTheGap"/>; the next one says how many were
    /// left unwritten in between (<see cref="RemSound.Core.LogDetailRule"/>).
    /// </summary>
    public void Trouble(string why, bool evenInsideTheGap = false)
    {
        TroubleTapForTest?.Invoke(why);
        if (!Enabled) return;
        string? said = null;
        lock (writeGate)
        {
            if (!enabled) return;
            if (detailRule.Trouble(UtcNowForTest(), why, evenInsideTheGap) is not { } burst) return;
            said = burst.Heading;
            WriteLineLocked($"EVT\t{DateTime.Now:o}\t{Sanitize(said)}");
            foreach (var line in burst.Lines) WriteLineLocked(line);
        }
        HeadlessRecords.Note(HeadlessRecords.Log, said);
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
    /// </summary>
    internal Action<string>? EventTapForTest;

    public void Event(string message)
    {
        EventTapForTest?.Invoke(message);
        HeadlessRecords.Note(HeadlessRecords.Log, message);
        if (!Enabled) return;
        lock (writeGate)
        {
            if (!enabled) return;
            WriteLineLocked($"EVT\t{DateTime.Now:o}\t{Sanitize(message)}");
        }
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
            writer?.WriteLine($"EVT\t{DateTime.Now:o}\tlog reached its size cap — continuing in a fresh file");
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
                if (writer is not null && Enabled)
                {
                    // Inline the "log stopped" write so we don't re-acquire the gate.
                    writer.WriteLine($"EVT\t{DateTime.Now:o}\tlog stopped");
                }
                writer?.Dispose();
            }
            catch { /* swallow */ }
        }
    }

    private static string Sanitize(string value) => value.Replace('\t', ' ').Replace('\n', ' ');
}
