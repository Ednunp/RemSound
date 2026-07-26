namespace RemSound.App;

/// <summary>
/// Lightweight tab-separated log file written next to the executable in <c>logs\</c>.
/// Two row kinds:
///   SNAP  — periodic snapshot of runtime counters (one per second)
///   EVT   — one-off events (connect, disconnect, codec change, errors)
/// Format:
///   SNAP\t{Timestamp}\t{Machine}\t{Connected}\t{SendRunning}\t{ReceiveRunning}\t{Codec}\t
///         {MaxLatencyMs}\t{TargetLatencyMs}\t{BufferMs}\t{SenderPackets}\t{SenderKB}\t
///         {SenderDevice}\t{ReceiverPackets}\t{ReceiverKB}\t{Underruns}\t{Drops}\t{ReceiveDevice}
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

    /// <summary>Master gate for all writes. When false, both <see cref="Event"/> and
    /// <see cref="Snapshot"/> short-circuit before touching the file system — no creation,
    /// no headers, no data. Defaults to false so the App can construct the log object
    /// before reading the user's preference; the App pushes the real value in after.</summary>
    public bool Enabled { get; set; }

    public RemSoundLog()
    {
        // No file work in the constructor. EnsureFileOpenLocked does it lazily on first
        // write, only when Enabled has been confirmed true.
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
            // The file ordinal makes a rolled file's name unique even when rolls land within the
            // same timestamp tick (tiny caps in the self-test roll several times per millisecond);
            // pid keeps concurrent instances apart as before.
            fileOrdinal++;
            var name = $"RemSound-{Sanitize(Environment.MachineName)}-{Environment.ProcessId}-{DateTime.Now:yyyyMMdd-HHmmss}-{fileOrdinal}.log";
            Path = System.IO.Path.Combine(dir, name);
            writer = new StreamWriter(new FileStream(Path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite))
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
            if (!EnsureFileOpenLocked()) return;
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
                writer!.WriteLine(line);
                AccountAndMaybeRollLocked(line.Length);
            }
            catch { /* swallow — log is best-effort */ }
        }
    }

    public void Event(string message)
    {
        if (!Enabled) return;
        lock (writeGate)
        {
            if (!EnsureFileOpenLocked()) return;
            try
            {
                var line = $"EVT\t{DateTime.Now:o}\t{message.Replace('\t', ' ').Replace('\n', ' ')}";
                writer!.WriteLine(line);
                AccountAndMaybeRollLocked(line.Length);
            }
            catch { /* swallow */ }
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
