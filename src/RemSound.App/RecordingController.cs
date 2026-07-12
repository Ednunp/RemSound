using System.Net;
using RemSound.Core;
using RemSound.Receiver;
using RemSound.Sender;

namespace RemSound.App;

/// <summary>
/// Glue between MainForm's Record menu and the actual recording pipeline. Owns the
/// lifecycle of the currently-running <see cref="AudioRecorder"/> (if any) and wires
/// the sender / receiver taps to it. Reading the user's saved settings, persisting
/// changes after the settings dialog, opening / changing the recordings folder — all
/// flow through here so MainForm stays focused on UI wiring.
///
/// Threading: the public methods are called from the UI thread only. The recorder
/// itself runs on its own background thread (it owns a queue + writer); the controller
/// just constructs and disposes it.
/// </summary>
internal sealed class RecordingController
{
    private readonly AudioSender sender;
    private readonly AudioReceiver receiver;
    private readonly RemSoundSettingsStore settings;
    private readonly Action<string> diagnostic;
    private AudioRecorder? active;
    // Multi-track (split) state — one track per connected peer (keyed by address), plus one for your own
    // send. Built entirely at Start and only replaced with null at Stop, so the audio-thread tap can read
    // the dictionary with a plain volatile read. Each PeerTrack sums that peer's stream(s) per render and
    // flushes once, so a peer sending on more than one lane is SUMMED (recorded as it sounds), not stacked.
    private volatile Dictionary<string, PeerTrack>? peerTracks;
    private AudioRecorder? meRecorder;

    private sealed class PeerTrack(AudioRecorder recorder)
    {
        public AudioRecorder Recorder { get; } = recorder;
        public float[] Accum = new float[4096];
        public int Len;
    }
    // Single-file BYPASS state — sum each peer's RAW block per render, flush on the block boundary.
    private float[] rawMixAccum = new float[4096];
    private int rawMixLen;

    /// <summary>Supplies the currently-connected peers (address + display name) at the moment a split
    /// recording starts, so one track file is created per peer. Set by MainForm.</summary>
    public Func<IReadOnlyList<(IPAddress Address, string Name)>>? ConnectedPeersProvider { get; set; }

    /// <summary>Test seam: overrides where <see cref="Start"/> reads the recording settings from, so a
    /// self-test can drive a split (multi-track) recording without writing to the real shared settings
    /// store. Null (the default) = read from the store as normal.</summary>
    internal Func<RecordingSettings>? SettingsSourceForTest { get; set; }

    public RecordingController(AudioSender sender, AudioReceiver receiver, RemSoundSettingsStore settings, Action<string> diagnostic)
    {
        this.sender = sender;
        this.receiver = receiver;
        this.settings = settings;
        this.diagnostic = diagnostic;
    }

    public bool IsRecording => active is not null || meRecorder is not null || peerTracks is not null;

    /// <summary>UTC clock at which the current recording started, or null when nothing is
    /// recording. Captured by <see cref="Start"/> and cleared by <see cref="Stop"/>. Used
    /// by the system-tray tooltip builder in MainForm to surface "recording for MM:SS"
    /// alongside the peer count. 2026-05-28.</summary>
    public DateTime? RecordingStartedUtc { get; private set; }

    /// <summary>Optional callback fired when the user starts or stops a recording. The
    /// MainForm hooks this to flip the menu item text "Start recording" ↔ "Stop recording"
    /// and announce the change to NVDA.</summary>
    public event Action<bool>? RecordingStateChanged;

    /// <summary>Start a new recording using the currently-saved profile settings. If a
    /// recording is already running this is a no-op (the menu shouldn't ever offer Start
    /// while recording, but the guard is here for safety).</summary>
    public void Start()
    {
        if (IsRecording) return;
        var s = (SettingsSourceForTest ?? settings.LoadRecordingSettings)();
        var now = DateTime.Now;
        try
        {
            if (s.SplitTracks) StartMultiTrack(s, now);
            else StartSingleTrack(s, now);
        }
        catch (Exception ex)
        {
            diagnostic($"recording: failed to start: {ex.GetType().Name}: {ex.Message}");
            CleanUpAfterFailedStart();
            MessageBox.Show(
                $"Could not start recording:\n\n{ex.Message}",
                "RemSound — recording",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }
        RecordingStartedUtc = DateTime.UtcNow;
        RecordingStateChanged?.Invoke(true);
    }

    /// <summary>Stop the currently-running recording. Unhooks taps, flushes the writer
    /// queue, closes the file, and surfaces the resulting path in a brief MessageBox
    /// so the user knows where the file landed.</summary>
    public void Stop()
    {
        if (!IsRecording) return;

        // Unhook every tap FIRST so no more audio is queued during the drain.
        sender.OnSentSamples = null;
        receiver.OnReceivedSamples = null;
        receiver.SetPeerRecordTap(null, false);
        receiver.OnRecordBlockComplete = null;

        var single = active;
        var me = meRecorder;
        var tracks = peerTracks;
        active = null;
        meRecorder = null;
        peerTracks = null;
        rawMixLen = 0;
        RecordingStartedUtc = null;

        StopRecorder(single);
        StopRecorder(me);
        if (tracks is not null) foreach (var t in tracks.Values) StopRecorder(t.Recorder);

        RecordingStateChanged?.Invoke(false);
    }

    private void OnRecorderFinished(string path, long bytes)
    {
        diagnostic($"recording: finished → {path} ({bytes:N0} bytes)");
    }

    private void StartSingleTrack(RecordingSettings s, DateTime now)
    {
        var path = SingleTrackPath(s, now);
        active = new AudioRecorder(s, diagnostic, OnRecorderFinished, path);
        sender.OnSentSamples = active.WriteSent;
        if (s.BypassShaping)
        {
            // Raw single file: the mixed receive tap is POST pan/EQ, so instead sum each peer's RAW
            // block per render and flush it to the one recorder on the block boundary.
            rawMixLen = 0;
            receiver.SetPeerRecordTap(OnRawMixTap, raw: true);
            receiver.OnRecordBlockComplete = FlushRawMix;
        }
        else
        {
            receiver.OnReceivedSamples = active.WriteReceived;   // the shaped mix — what you hear
        }
        diagnostic($"recording: started → {path} (source={s.Source}, format={s.FileFormat}, bypass={s.BypassShaping})");
    }

    private void StartMultiTrack(RecordingSettings s, DateTime now)
    {
        // Refuse a split received-only recording with no peers connected: it would make an empty dated
        // folder and capture nothing while still announcing "recording started". Fail clearly instead
        // (Start()'s catch surfaces this message). Both / sent-only still record your own send, so they
        // never hit this. Checked BEFORE creating the folder so no orphan folder is left behind.
        if (s.Source == RecordingSource.ReceivedOnly && (ConnectedPeersProvider?.Invoke() ?? []).Count == 0)
        {
            throw new InvalidOperationException(
                "No peers are connected, so a split received-only recording would capture nothing. " +
                "Connect to a peer first, or set the recording source to include your own sent audio.");
        }

        var folder = MultiTrackFolder(s, now);
        Directory.CreateDirectory(folder);
        var ext = AudioRecorder.ExtensionFor(s.FileFormat);
        var time = now.ToString("HH-mm-ss");

        // Multi-track follows the Source setting like single-file recording does: peer (received)
        // tracks unless "sent only", and your own (sent) track only when you're actually recording
        // your send ("both" or "sent only") — so a receive-only recording no longer makes a "me" file.

        // One track per connected peer (their received audio only), created up front on the UI thread
        // so the audio-thread tap never has to open a file. Peers that join mid-recording aren't added.
        if (s.Source != RecordingSource.SentOnly)
        {
            var recs = new Dictionary<string, PeerTrack>();
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var byAddr = new Dictionary<string, string>();
            foreach (var (addr, name) in ConnectedPeersProvider?.Invoke() ?? []) byAddr[addr.ToString()] = name;
            foreach (var (addrKey, name) in byAddr)
            {
                var baseName = Sanitize(string.IsNullOrWhiteSpace(name) ? addrKey : name);
                var fileName = $"{baseName} {time}";
                if (!usedNames.Add(fileName)) fileName = $"{baseName} ({addrKey}) {time}";
                var path = Path.Combine(folder, $"{fileName}.{ext}");
                recs[addrKey] = new PeerTrack(new AudioRecorder(WithSource(s, RecordingSource.ReceivedOnly), diagnostic, OnRecorderFinished, path));
            }
            peerTracks = recs;
            receiver.SetPeerRecordTap(OnPeerRecordBlock, raw: s.BypassShaping);
            receiver.OnRecordBlockComplete = FlushPeerTracks;
        }

        // Your own track — your sent audio.
        if (s.Source != RecordingSource.ReceivedOnly)
        {
            var mePath = Path.Combine(folder, $"{Sanitize(Environment.MachineName)} {time}.{ext}");
            meRecorder = new AudioRecorder(WithSource(s, RecordingSource.SentOnly), diagnostic, OnRecorderFinished, mePath);
            sender.OnSentSamples = meRecorder.WriteSent;
        }

        diagnostic($"recording: started multi-track → {folder} ({(peerTracks?.Count ?? 0)} peer track(s){(meRecorder is not null ? " + your send" : "")}, source={s.Source}, format={s.FileFormat}, bypass={s.BypassShaping})");
    }

    // Audio thread. Route each peer's block to that peer's recorder. peerRecorders is fully built at
    // Start and only replaced with null at Stop, so a plain volatile read is safe.
    private void OnPeerRecordBlock(IPEndPoint peer, ReadOnlyMemory<float> block)
    {
        var tracks = peerTracks;
        if (tracks is null || !tracks.TryGetValue(peer.Address.ToString(), out var t)) return;
        // SUM this peer's block into their per-render buffer, so a peer sending on more than one lane is
        // combined (recorded as it sounds) rather than the lanes stacking one after another.
        var span = block.Span;
        if (t.Accum.Length < span.Length) t.Accum = new float[span.Length];
        for (int i = 0; i < span.Length; i++) t.Accum[i] += span[i];
        t.Len = span.Length;
    }

    // Audio thread. Once per render, flush each peer's summed block to their file. Every peer track gets
    // EXACTLY one render block per callback: a peer that produced nothing this block — because it went
    // quiet and its session was dropped, or it disconnected — is padded with silence to the same length.
    // That keeps every track sample-locked to the single render clock, so the tracks can never drift
    // apart from each other however long the recording runs, and they all end up the same length.
    private void FlushPeerTracks(int floats)
    {
        var tracks = peerTracks;
        if (tracks is null || floats <= 0) return;
        foreach (var t in tracks.Values)
        {
            if (t.Accum.Length < floats) { var a = t.Accum; Array.Resize(ref a, floats); t.Accum = a; }
            for (int i = t.Len; i < floats; i++) t.Accum[i] = 0f;   // pad an empty/short block with silence
            t.Recorder.WriteReceived(t.Accum.AsMemory(0, floats), RenderRoute.Mixed);
            Array.Clear(t.Accum, 0, floats);
            t.Len = 0;
        }
    }

    // Audio thread. Sum a peer's raw block into the per-render mix for a single-file bypass recording.
    private void OnRawMixTap(IPEndPoint peer, ReadOnlyMemory<float> block)
    {
        var span = block.Span;
        if (rawMixAccum.Length < span.Length) rawMixAccum = new float[span.Length];
        for (int i = 0; i < span.Length; i++) rawMixAccum[i] += span[i];
        rawMixLen = span.Length;
    }

    // Audio thread. Flush the summed raw mix for this render to the single recorder, padding a silent
    // block so the file stays aligned to real time even through total silence, then reset.
    private void FlushRawMix(int floats)
    {
        var rec = active;
        if (rec is null || floats <= 0) return;
        if (rawMixAccum.Length < floats) { var a = rawMixAccum; Array.Resize(ref a, floats); rawMixAccum = a; }
        for (int i = rawMixLen; i < floats; i++) rawMixAccum[i] = 0f;
        rec.WriteReceived(rawMixAccum.AsMemory(0, floats), RenderRoute.Mixed);
        Array.Clear(rawMixAccum, 0, floats);
        rawMixLen = 0;
    }

    private void StopRecorder(AudioRecorder? r)
    {
        if (r is null) return;
        try { r.Stop(); r.Dispose(); }
        catch (Exception ex) { diagnostic($"recording: stop threw {ex.GetType().Name}: {ex.Message}"); }
    }

    private void CleanUpAfterFailedStart()
    {
        sender.OnSentSamples = null;
        receiver.OnReceivedSamples = null;
        receiver.SetPeerRecordTap(null, false);
        receiver.OnRecordBlockComplete = null;
        StopRecorder(active); active = null;
        StopRecorder(meRecorder); meRecorder = null;
        if (peerTracks is not null) { foreach (var t in peerTracks.Values) StopRecorder(t.Recorder); peerTracks = null; }
    }

    private static string BaseFolder(RecordingSettings s)
    {
        var f = s.ResolvedFolder();
        return string.IsNullOrWhiteSpace(f) ? RecordingSettings.DefaultFolder() : f;
    }

    private static string DateFolder(RecordingSettings s, DateTime now) =>
        Path.Combine(BaseFolder(s), now.ToString("yyyy-MM-dd"));

    private static string SingleTrackPath(RecordingSettings s, DateTime now) =>
        Path.Combine(DateFolder(s, now), $"{now:HH-mm-ss} RemSound recording {Sanitize(Environment.MachineName)}.{AudioRecorder.ExtensionFor(s.FileFormat)}");

    private static string MultiTrackFolder(RecordingSettings s, DateTime now) =>
        Path.Combine(DateFolder(s, now), $"{now:HH-mm-ss} RemSound recording multi track");

    private static RecordingSettings WithSource(RecordingSettings s, RecordingSource src)
    {
        var c = s.Clone();
        c.Source = src;
        return c;
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var ch in name) sb.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
        var r = sb.ToString().Trim().TrimEnd('.').Trim();
        return string.IsNullOrEmpty(r) ? "peer" : r;
    }

    /// <summary>Open the currently-configured recordings folder in Windows Explorer.
    /// Creates the folder if it doesn't yet exist (a fresh install hasn't recorded
    /// anything, so the folder won't be there). Surfaces filesystem errors to the user
    /// rather than swallowing them silently.</summary>
    public void OpenCurrentFolder(IWin32Window? owner)
    {
        var s = settings.LoadRecordingSettings();
        var folder = s.ResolvedFolder();
        try
        {
            Directory.CreateDirectory(folder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = folder,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            diagnostic($"recording: open folder failed: {ex.GetType().Name}: {ex.Message}");
            MessageBox.Show(owner,
                $"Could not open recordings folder:\n\n{ex.Message}",
                "RemSound — recordings folder",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    /// <summary>Show a folder-picker rooted at the current recordings folder. If the
    /// user picks a different folder, save it on the profile and return true so the
    /// caller can flag the profile dirty.</summary>
    public bool ChangeFolder(IWin32Window? owner)
    {
        var s = settings.LoadRecordingSettings();
        var startFolder = s.ResolvedFolder();
        using var picker = new FolderBrowserDialog
        {
            Description = "Choose a folder for RemSound recordings",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(startFolder) ? startFolder : RecordingSettings.DefaultFolder(),
            ShowNewFolderButton = true,
        };
        if (picker.ShowDialog(owner) != DialogResult.OK) return false;
        if (string.IsNullOrWhiteSpace(picker.SelectedPath)) return false;
        if (string.Equals(picker.SelectedPath, startFolder, StringComparison.OrdinalIgnoreCase)) return false;

        s.Folder = picker.SelectedPath;
        settings.SaveRecordingSettings(s);
        diagnostic($"recording: folder changed → {picker.SelectedPath}");
        return true;
    }
}
