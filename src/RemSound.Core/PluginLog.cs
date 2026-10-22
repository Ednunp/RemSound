using System.Text;

namespace RemSound.Core;

/// <summary>
/// The VST plugin's own log file.
///
/// <para><b>Why a separate file.</b> The plugin runs inside the DAW's process, not RemSound's, so it
/// cannot write to the app's log — two processes sharing one file would interleave into nonsense.
/// It lands in the SAME <c>logs\</c> folder with an obvious name, so somebody sending logs in picks
/// up both without being told about a second place to look.</para>
///
/// <para><b>Gated by the same switch.</b> "Enable logs" in RemSound's Preferences, read here from the
/// shared machine-wide config. Off means no file is created at all — not an empty one. The setting is
/// re-read periodically, so turning logging on while a DAW session is open starts a file without
/// reloading the plugin, which matters when the interesting fault has already started.</para>
///
/// <para><b>Nothing here is called from the audio thread.</b> That thread only bumps counters (see
/// <see cref="Count"/>, which is a lock-free add); the once-a-second readings (to the readings file, a row a minute and
/// every second around a problem - <see cref="ReadingsRecorder"/>) and every event line are written from a background
/// timer. Writing a line from a DAW's audio callback would be a file system call in the middle of somebody's take.</para>
/// </summary>
public sealed class PluginLog : IDisposable
{
    /// <summary>The plugin's readings file (2026-10-22, issue 35): what it said every second as named columns, beside its log,
    /// as the app's are (<see cref="ReadingsRecorder"/>) - a row for every minute, and every second around late sound or
    /// RemSound not answering.</summary>
    public static readonly ReadingsSchema Schema = new(
    [
        new("job", ReadingAgg.Last, Text: true), new("peer", ReadingAgg.Last, Text: true), new("connected", ReadingAgg.Last),
        new("hostRate", ReadingAgg.Last), new("blockFrames", ReadingAgg.Last), new("resampling", ReadingAgg.Last),
        new("blocksOut", ReadingAgg.Last), new("blocksIn", ReadingAgg.Last), new("shortBlocksTotal", ReadingAgg.Last),
        new("lateBlocks", ReadingAgg.Sum), new("ringFrames", ReadingAgg.Mean), new("ringFramesMin", ReadingAgg.Min),
        new("bytesOut", ReadingAgg.Last), new("bytesIn", ReadingAgg.Last), new("knownPeers", ReadingAgg.Last),
        new("hostCalls", ReadingAgg.Last), new("lastFrames", ReadingAgg.Last), new("submitted", ReadingAgg.Last),
        new("busFlushed", ReadingAgg.Last), new("busSenders", ReadingAgg.Last), new("inputPeakDb", ReadingAgg.Max),
    ]);

    private readonly object writeGate = new();
    private readonly Guid instanceId;
    private StreamWriter? writer;
    private bool creationFailed;
    private long bytesWritten;
    private int fileOrdinal;
    /// <summary>The name this instance's first file settled on (LogFileNames): every later part carries it, with "-partN",
    /// and so does its readings file.</summary>
    private string? sessionStem;
    private ReadingsRecorder? readings;
    private StreamWriter? readingsWriter;
    /// <summary>Runs of late sound, as the app's runs of gaps (GapEpisodes): a capture when one starts or gets clearly
    /// worse, a line when it ends.</summary>
    private readonly GapEpisodes lateSound = new();
    private long lastShortBlocks = -1;
    private bool wasConnected;
    /// <summary>The clock the log runs on (a test seam): this computer's time.</summary>
    internal Func<DateTime> NowForTest { get; set; } = () => DateTime.Now;
    /// <summary>One second's readings, on demand: the gate steps the clock and pauses the timer (<see cref="Pause"/>).</summary>
    internal void WriteSnapshotForTest() => WriteSnapshot();
    // Explicitly System.Threading: Core has WinForms in scope, and a WinForms timer needs a message
    // loop the DAW does not give us.
    private System.Threading.Timer? snapshotTimer;
    private DateTime lastEnabledCheck = DateTime.MinValue;
    private bool enabled;

    /// <summary>Where RemSound keeps its logs, learned from the pointer file. Null until it is read.</summary>
    private string? logsDirectory;

    /// <summary>Cap on one file, then a fresh one starts. A DAW session left open all day with logging
    /// on would otherwise grow a single file without bound.</summary>
    internal long RollAfterBytes { get; set; } = 20L * 1024 * 1024;

    /// <summary>Where the file went, once there is one. Null until the first write with logging on,
    /// and null again if creation failed — never a path to a file this log does not own.</summary>
    public string? Path { get; private set; }

    /// <summary>The readings file, once its first row is written.</summary>
    public string? ReadingsPath { get; private set; }

    /// <summary>How many seconds' readings have been taken. The gate waits on this rather than polling the files: reading
    /// a file its own writer still holds open is a sharing fight that proves nothing about the logging.</summary>
    internal long SnapshotsWritten { get; private set; }

    /// <summary>What the once-a-second readings report. Set from the plugin's own state; read on the
    /// timer thread, so every field is written as a whole value rather than built up in pieces.</summary>
    public Func<PluginLogSnapshot>? SnapshotSource { get; set; }

    public PluginLog(Guid instanceId)
    {
        this.instanceId = instanceId;
        snapshotTimer = new System.Threading.Timer(_ => WriteSnapshot(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    /// <summary>Is logging on? Re-read from the shared config at most once a second — cheap enough for
    /// the timer, and it means the switch takes effect without reloading the plugin.</summary>
    private bool Enabled
    {
        get
        {
            var now = DateTime.UtcNow;
            if (now - lastEnabledCheck < TimeSpan.FromSeconds(1)) return enabled;
            lastEnabledCheck = now;
            try
            {
                // The POINTER, not AppConfig's own paths. Inside a DAW, AppContext.BaseDirectory is
                // the DAW's folder, so AppConfig would look for RemSound's settings somewhere it has
                // never been — which is exactly why the first build wrote no plugin log anywhere.
                var pointer = AppConfig.ReadPluginPointer();
                enabled = pointer?.LoggingEnabled ?? false;
                logsDirectory = pointer is null ? null : System.IO.Path.Combine(pointer.Value.UserDataDirectory, "logs");
            }
            catch { enabled = false; }   // a config we cannot read is not a reason to break a DAW
            return enabled && logsDirectory is not null;
        }
    }

    /// <summary>One thing that happened, in plain English, so the file is readable by whoever is sent it. Its severity
    /// and category come from its words (LogLines); anything not otherwise sorted is the plugin's.</summary>
    public void Event(string message)
    {
        var (level, category, text) = LogLines.Classify(message);
        Event(level, category == "App" ? "Plugin" : category, text);
    }

    public void Event(LogLevel level, string category, string text)
    {
        if (!Enabled) return;
        lock (writeGate)
        {
            if (!EnsureOpenLocked()) return;
            WriteLineLocked(LogLines.Format(level, NowForTest(), category, text));
        }
    }

    /// <summary>Stop the once-a-second readings while the instance is deactivated - a plugin removed from its track would
    /// otherwise take one a second until the DAW closed - and start them again.</summary>
    public void Pause() { try { snapshotTimer?.Change(Timeout.Infinite, Timeout.Infinite); } catch (ObjectDisposedException) { } }

    public void Resume() { try { snapshotTimer?.Change(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)); } catch (ObjectDisposedException) { } }

    private void WriteSnapshot()
    {
        var source = SnapshotSource;
        if (source is null) return;
        if (!Enabled)
        {
            // Off means off: nothing waiting from before may reach the file if logging comes back on.
            lock (writeGate) readings?.Forget();
            return;
        }
        PluginLogSnapshot snap;
        try { snap = source(); }
        catch { return; }   // a snapshot that throws must never take the timer thread down

        lock (writeGate)
        {
            if (!EnsureOpenLocked()) return;
            var now = NowForTest();
            var late = lastShortBlocks >= 0 && snap.ShortBlocks > lastShortBlocks ? snap.ShortBlocks - lastShortBlocks : 0;
            var row = new ReadingsRow(Schema, [])
                .Text("job", snap.Job).Text("peer", snap.Peer ?? "-").Set("connected", snap.Connected)
                .Set("hostRate", snap.HostSampleRate).Set("blockFrames", snap.BlockFrames).Set("resampling", snap.Resampling)
                .Set("blocksOut", snap.BlocksOut).Set("blocksIn", snap.BlocksIn).Set("shortBlocksTotal", snap.ShortBlocks)
                .Set("lateBlocks", late).Set("ringFrames", snap.RingFrames).Set("ringFramesMin", snap.RingFrames)
                .Set("bytesOut", snap.BytesOut).Set("bytesIn", snap.BytesIn).Set("knownPeers", snap.KnownPeers)
                .Set("hostCalls", snap.HostCalls).Set("lastFrames", snap.LastFrames).Set("submitted", snap.Submitted)
                .Set("busFlushed", snap.BusFlushed).Set("busSenders", snap.BusSenders);
            if (snap.InputPeakDb > -120) row.Set("inputPeakDb", Math.Round(snap.InputPeakDb, 1));
            readings ??= new ReadingsRecorder(Schema, WriteReadingLineLocked);
            // The rows first, so this second is among the ones a capture keeps. Then: sound arriving late on this track (a
            // run of it, as the app's runs of gaps), or RemSound no longer answering (the five minutes before it kept).
            readings.Second(now, row);
            SnapshotsWritten++;
            foreach (var change in lateSound.Second(now, late, 0))
            {
                var words = change.Words.Replace("gaps", "late blocks").Replace("gap", "late block");
                if (change.Kind is GapEpisodes.Kind.Started or GapEpisodes.Kind.Worse)
                {
                    var (from, to) = readings.Capture(now, TimeSpan.FromSeconds(30));
                    words += $" - every second from {from:HH:mm:ss} to {to:HH:mm:ss} goes in the readings file";
                }
                WriteLineLocked(LogLines.Format(change.Kind == GapEpisodes.Kind.StillGoing ? LogLevel.Info : LogLevel.Warn, now, "Plugin", words));
            }
            if (wasConnected && !snap.Connected)
            {
                var (from, to) = readings.Capture(now, TimeSpan.FromMinutes(5));
                WriteLineLocked(LogLines.Format(LogLevel.Warn, now, "Plugin",
                    $"RemSound stopped answering - every second from {from:HH:mm:ss} to {to:HH:mm:ss} goes in the readings file"));
            }
            lastShortBlocks = snap.ShortBlocks;
            wasConnected = snap.Connected;
        }
    }

    private bool EnsureOpenLocked()
    {
        if (writer is not null) return true;
        if (creationFailed) return false;
        try
        {
            var dir = logsDirectory ?? AppConfig.LogsDirectory;
            Directory.CreateDirectory(dir);
            fileOrdinal++;
            // Named so it sorts next to the app's logs, by when it started, and is obviously the plugin's
            // (LogFileNames, 2026-10-19).
            //
            // The INSTANCE id is in the name, not just in a column. A DAW loads every plugin instance
            // into ONE process, so two RemSound tracks would otherwise race for the same filename
            // within the same second — the first would win and the second would silently never log,
            // which is precisely the two-track case a tester is most likely to be reporting on.
            var (path, stream, stem) = LogFileNames.CreateNew(dir, sessionStem ?? LogFileNames.PluginStem(DateTime.Now, Short(instanceId)), fileOrdinal);
            sessionStem = stem;
            Path = path;
            writer = new StreamWriter(stream, Encoding.UTF8)
            {
                AutoFlush = true,   // a DAW that hangs must still leave the log that explains why
            };
            bytesWritten = 0;
            var now = NowForTest();
            writer.WriteLine(LogLines.Format(LogLevel.Info, now, "Plugin",
                $"plugin log {(fileOrdinal == 1 ? "started" : "continued")} {now:dddd d MMMM yyyy}, instance {Short(instanceId)}, host process "
                + $"{Environment.ProcessId}. What it measures every second is in {stem}-readings.csv beside this file."));
            return true;
        }
        catch
        {
            // No write access to the logs folder, a full disk, a locked file — none of which is worth
            // interrupting somebody's session over. Give up quietly and permanently.
            //
            // Path is CLEARED, not left pointing at the name we tried. A path to a file we do not own
            // is worse than no path: anything that reads it back is reading somebody else's file.
            creationFailed = true;
            writer = null;
            Path = null;
            return false;
        }
    }

    private void WriteReadingLineLocked(string line)
    {
        try
        {
            if (readingsWriter is null)
            {
                if (sessionStem is null || Path is null) return;
                ReadingsPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path)!, $"{sessionStem}-readings.csv");
                readingsWriter = new StreamWriter(new FileStream(ReadingsPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), Encoding.UTF8) { AutoFlush = true };
            }
            readingsWriter.WriteLine(line);
        }
        catch
        {
            try { readingsWriter?.Dispose(); } catch { }
            readingsWriter = null;
        }
    }

    private void WriteLineLocked(string line)
    {
        try
        {
            writer!.WriteLine(line);
            bytesWritten += line.Length + 2;
            if (bytesWritten < RollAfterBytes) return;
            writer.WriteLine(LogLines.Format(LogLevel.Info, NowForTest(), "Plugin", "this file has reached its size limit - the log goes on in a fresh file"));
            writer.Dispose();
            writer = null;
        }
        catch
        {
            // A failed write must not throw into whatever called us. Drop the writer so the next
            // attempt reopens rather than repeatedly failing on a broken handle.
            try { writer?.Dispose(); } catch { }
            writer = null;
        }
    }

    private static string Short(Guid id) => id.ToString("N")[..8];

    public void Dispose()
    {
        snapshotTimer?.Dispose();
        snapshotTimer = null;
        lock (writeGate)
        {
            try { if (enabled) readings?.Flush(NowForTest(), closeMinute: true); } catch { }
            try { writer?.Dispose(); } catch { }
            try { readingsWriter?.Dispose(); } catch { }
            writer = null;
            readingsWriter = null;
        }
    }
}

/// <summary>One second of plugin state, as the log records it. A record rather than loose parameters
/// so a new field cannot be silently dropped from the line by a caller that wasn't updated.</summary>
public sealed record PluginLogSnapshot(
    string Job,
    string? Peer,
    bool Connected,
    double HostSampleRate,
    int BlockFrames,
    bool Resampling,
    long BlocksOut,
    long BlocksIn,
    long ShortBlocks,
    int RingFrames,
    long BytesOut,
    long BytesIn,
    int KnownPeers,
    // Where a send stops, when it stops (2026-09-24: in Reaper the plugin sent for two seconds of playback and then
    // nothing, with every other column saying all was well). How often the host called us, the size of its last
    // block, how many blocks we handed to the send bus, how many the bus got out, how many senders it had, and the
    // loudest sample coming IN since the last line - blocks can flow and still carry silence.
    long HostCalls = 0,
    int LastFrames = 0,
    long Submitted = 0,
    long BusFlushed = 0,
    int BusSenders = 0,
    double InputPeakDb = -200);
