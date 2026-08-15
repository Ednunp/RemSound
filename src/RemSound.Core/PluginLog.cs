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
/// <see cref="Count"/>, which is a lock-free add); the once-a-second snapshot and every event line are
/// written from a background timer. Writing a line from a DAW's audio callback would be a file system
/// call in the middle of somebody's take.</para>
/// </summary>
public sealed class PluginLog : IDisposable
{
    private const string SnapHeader =
        "Kind\tTimestamp\tInstance\tJob\tPeer\tConnected\tHostRate\tBlockFrames\tResampling\t" +
        "BlocksOut\tBlocksIn\tShortBlocks\tRingFrames\tBytesOut\tBytesIn\tKnownPeers";

    private readonly object writeGate = new();
    private readonly Guid instanceId;
    private StreamWriter? writer;
    private bool creationFailed;
    private long bytesWritten;
    private int fileOrdinal;
    // Explicitly System.Threading: Core has WinForms in scope, and a WinForms timer needs a message
    // loop the DAW does not give us.
    private System.Threading.Timer? snapshotTimer;
    private DateTime lastEnabledCheck = DateTime.MinValue;
    private bool enabled;

    /// <summary>Cap on one file, then a fresh one starts. A DAW session left open all day with logging
    /// on would otherwise grow a single file without bound.</summary>
    internal long RollAfterBytes { get; set; } = 20L * 1024 * 1024;

    /// <summary>Where the file went, once there is one. Null until the first write with logging on,
    /// and null again if creation failed — never a path to a file this log does not own.</summary>
    public string? Path { get; private set; }

    /// <summary>How many per-second lines have been written. The gate waits on this rather than
    /// polling the file: reading a log its own writer still holds open is a sharing fight that proves
    /// nothing about the logging.</summary>
    internal long SnapshotsWritten { get; private set; }

    /// <summary>What the once-a-second line reports. Set from the plugin's own state; read on the
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
            try { enabled = AppConfig.Load().LoggingEnabled; }
            catch { enabled = false; }   // a config we cannot read is not a reason to break a DAW
            return enabled;
        }
    }

    /// <summary>One thing that happened. Free-text, plain English, so the file is readable by whoever
    /// is sent it rather than only by whoever wrote the code.</summary>
    public void Event(string message)
    {
        if (!Enabled) return;
        lock (writeGate)
        {
            if (!EnsureOpenLocked()) return;
            WriteLineLocked($"EVT\t{Stamp()}\t{Short(instanceId)}\t{message}");
        }
    }

    private void WriteSnapshot()
    {
        var source = SnapshotSource;
        if (source is null || !Enabled) return;
        PluginLogSnapshot snap;
        try { snap = source(); }
        catch { return; }   // a snapshot that throws must never take the timer thread down

        lock (writeGate)
        {
            if (!EnsureOpenLocked()) return;
            SnapshotsWritten++;
            WriteLineLocked(string.Join('\t',
                "SNAP", Stamp(), Short(instanceId), snap.Job, snap.Peer ?? "-", snap.Connected ? "yes" : "no",
                snap.HostSampleRate.ToString("0"), snap.BlockFrames, snap.Resampling ? "yes" : "no",
                snap.BlocksOut, snap.BlocksIn, snap.ShortBlocks, snap.RingFrames,
                snap.BytesOut, snap.BytesIn, snap.KnownPeers));
        }
    }

    private bool EnsureOpenLocked()
    {
        if (writer is not null) return true;
        if (creationFailed) return false;
        try
        {
            var dir = AppConfig.LogsDirectory;
            Directory.CreateDirectory(dir);
            fileOrdinal++;
            // Named so it sorts next to the app's logs and is obviously the plugin's.
            //
            // The INSTANCE id is in the name, not just in a column. A DAW loads every plugin instance
            // into ONE process, so two RemSound tracks would otherwise race for the same filename
            // within the same second — the first would win and the second would silently never log,
            // which is precisely the two-track case a tester is most likely to be reporting on.
            var name = $"RemSoundPlugin-{Sanitize(Environment.MachineName)}-{Environment.ProcessId}-{Short(instanceId)}"
                     + $"-{DateTime.Now:yyyyMMdd-HHmmss}-{fileOrdinal}.log";
            Path = System.IO.Path.Combine(dir, name);
            writer = new StreamWriter(new FileStream(Path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite), Encoding.UTF8)
            {
                AutoFlush = true,   // a DAW that hangs must still leave the log that explains why
            };
            bytesWritten = 0;
            writer.WriteLine(SnapHeader);
            writer.WriteLine($"EVT\t{Stamp()}\t{Short(instanceId)}\tplugin log started (host process {Environment.ProcessId})");
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

    private void WriteLineLocked(string line)
    {
        try
        {
            writer!.WriteLine(line);
            bytesWritten += line.Length + 2;
            if (bytesWritten < RollAfterBytes) return;
            writer.WriteLine($"EVT\t{Stamp()}\t{Short(instanceId)}\tfile full, continued in the next one");
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

    private static string Stamp() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
    private static string Short(Guid id) => id.ToString("N")[..8];
    private static string Sanitize(string s) => string.Concat(s.Select(c => char.IsLetterOrDigit(c) ? c : '-'));

    public void Dispose()
    {
        snapshotTimer?.Dispose();
        snapshotTimer = null;
        lock (writeGate)
        {
            try { writer?.Dispose(); } catch { }
            writer = null;
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
    int KnownPeers);
