namespace RemSound.Core;

/// <summary>What audio gets captured by the recorder. Selected in the Recording settings
/// dialog and saved per profile. Defaults to <see cref="ReceivedOnly"/> which is the most
/// common "I want a copy of what my collaborator just played me" case.</summary>
public enum RecordingSource
{
    /// <summary>Record only audio coming from connected peers (everything that would play
    /// out of the receiver's render path).</summary>
    ReceivedOnly = 0,
    /// <summary>Record only audio captured locally (everything this machine is sending to
    /// peers — your own mics / loopback / ASIO inputs).</summary>
    SentOnly = 1,
    /// <summary>Record the sum of received + sent audio, soft-mixed and limiter-protected
    /// just like the playback path. Useful for capturing a complete two-way exchange in a
    /// single file.</summary>
    Both = 2,
}

/// <summary>Output container format the recorder writes to disk. The format dictates the
/// shape of <see cref="RecordingSettings.AudioAttributes"/> — uncompressed formats take
/// bit-depth, compressed formats take a bitrate, mono/stereo applies to all of them.</summary>
public enum RecordingFileFormat
{
    /// <summary>RIFF WAVE, PCM. Lossless, large. Writer: in-process custom WAV writer with
    /// periodic header re-patching so a mid-session crash leaves a playable file.</summary>
    Wav = 0,
    /// <summary>MPEG Layer III. Lossy, small. Writer: NAudio.Lame (LAME library).</summary>
    Mp3 = 1,
    /// <summary>Ogg container with Opus codec. Lossy, very small at sane bitrates.
    /// Reuses the Concentus Opus encoder the wire path uses, wrapped in an Ogg container
    /// via the Concentus.Oggfile NuGet.</summary>
    Ogg = 2,
    /// <summary>FLAC — lossless, typically ~50 % the size of equivalent WAV.
    /// Writer: CUETools.Codecs.FLAKE — pure-managed FLAC encoder, no native DLL.</summary>
    Flac = 3,
}

/// <summary>Channel layout for the recording — independent of the format. Stereo preserves
/// the L/R as captured; Mono downmixes to (L + R) / 2 with a 3 dB headroom safety knock so
/// fully-correlated content doesn't clip.</summary>
public enum RecordingChannelMode
{
    Stereo = 0,
    Mono = 1,
}

/// <summary>All the user-selectable knobs for a recording. Stored on <see cref="Profile"/>.
///
/// The <see cref="AudioAttributes"/> field is a flat int that means different things per
/// format — <see cref="WavBitsPerSample"/> for WAV, <see cref="Mp3BitrateKbps"/> for MP3
/// — kept as one slot rather than a separate field per format because (a) only one is
/// active at a time and (b) it keeps the profile JSON narrow. The format enum decides
/// which interpretation applies.
///
/// Path policy: <see cref="Folder"/> is stored verbatim. When empty, recordings go to
/// the default location (<c>&lt;exe&gt;\recordings\&lt;machine&gt;\</c>). When set, it
/// IS the folder — no per-machine subfolder is appended. Each recording session creates
/// a new file inside the folder, named with a UTC timestamp and the format extension.
/// </summary>
public sealed class RecordingSettings
{
    public RecordingSource Source { get; set; } = RecordingSource.ReceivedOnly;
    public RecordingFileFormat FileFormat { get; set; } = RecordingFileFormat.Wav;
    public RecordingChannelMode ChannelMode { get; set; } = RecordingChannelMode.Stereo;

    /// <summary>WAV bit depth. 16 / 24 / 32. 32 means IEEE float; 16 and 24 are signed PCM.
    /// Defaults to 24 which matches RemSound's on-wire PCM bit depth — no extra quantisation
    /// happens on the way to disk. Ignored when <see cref="FileFormat"/> isn't WAV.</summary>
    public int WavBitsPerSample { get; set; } = 24;

    /// <summary>MP3 CBR bitrate in kbps. Common values: 128, 192, 256, 320. 320 is the
    /// LAME maximum and the default here — the recording feature is for archival of audio
    /// you cared enough to send over the network, not for a podcast feed, so the bias is
    /// toward "make the file slightly bigger for an audibly cleaner result". Ignored when
    /// <see cref="FileFormat"/> isn't MP3.</summary>
    public int Mp3BitrateKbps { get; set; } = 320;

    /// <summary>OGG-Opus VBR target bitrate in kbps. Opus' practical sweet spot for music
    /// is 96–256 kbps; below 96 starts to introduce audible artefacts on dense material,
    /// above 256 is diminishing returns. Default 192 — same compromise as the MP3 default
    /// "noticeably-larger file for noticeably-cleaner result". Ignored when
    /// <see cref="FileFormat"/> isn't Ogg.</summary>
    public int OggOpusBitrateKbps { get; set; } = 192;

    /// <summary>FLAC bit depth. 16 or 24 — FLAC is integer-PCM only, no 32-bit float, so
    /// the WAV "32-bit float" option doesn't carry over. 24-bit matches the wire PCM bit
    /// depth and is the default. Ignored when <see cref="FileFormat"/> isn't FLAC.</summary>
    public int FlacBitsPerSample { get; set; } = 24;

    /// <summary>FLAC compression level, 0–8. Higher = smaller file, more CPU during encode;
    /// all levels are losslessly identical on decode. Reference encoder default is 5; we
    /// match that — the encode is comfortably real-time at level 5 on any modern CPU.
    /// Ignored when <see cref="FileFormat"/> isn't FLAC.</summary>
    public int FlacCompressionLevel { get; set; } = 5;

    /// <summary>Absolute path to the folder recordings get written into. Empty / null
    /// means "use the default <c>&lt;exe&gt;\recordings\&lt;machine&gt;\</c>". Persisted
    /// verbatim — if a saved profile points at a folder that doesn't exist on the loading
    /// machine, the recorder falls back to the default and notes it in the diagnostics.</summary>
    public string? Folder { get; set; }

    /// <summary>When true, a recording is written as a FOLDER of separate tracks instead of one mixed
    /// file: one file per connected peer (only that peer's received audio) plus one file for your own
    /// sent audio. Off by default. (Ed's multi-track request.)</summary>
    public bool SplitTracks { get; set; }

    /// <summary>When true, per-peer pan and EQ are BYPASSED for the recording — it captures the raw,
    /// unshaped audio even though you still hear the shaped version live. When false (default), your
    /// pan/EQ are baked into the recording (and on a split recording, into each peer's own track).</summary>
    public bool BypassShaping { get; set; }

    public RecordingSettings Clone() => new()
    {
        Source = Source,
        FileFormat = FileFormat,
        ChannelMode = ChannelMode,
        WavBitsPerSample = WavBitsPerSample,
        Mp3BitrateKbps = Mp3BitrateKbps,
        OggOpusBitrateKbps = OggOpusBitrateKbps,
        FlacBitsPerSample = FlacBitsPerSample,
        FlacCompressionLevel = FlacCompressionLevel,
        Folder = Folder,
        SplitTracks = SplitTracks,
        BypassShaping = BypassShaping,
    };

    /// <summary>Default folder path used when <see cref="Folder"/> is blank. Computed at
    /// call time (not cached) so a launch from a different exe directory picks up that
    /// directory rather than the first-load one. Recordings nest by DATE inside this — the
    /// machine name lives in the split-track file names, not a top-level folder.</summary>
    public static string DefaultFolder() =>
        Path.Combine(AppContext.BaseDirectory, "recordings");

    /// <summary>Returns the resolved folder this profile would record into right now —
    /// either the explicit <see cref="Folder"/> if set, or <see cref="DefaultFolder"/>.
    /// Does not create the folder on disk.</summary>
    public string ResolvedFolder() =>
        string.IsNullOrWhiteSpace(Folder) ? DefaultFolder() : Folder!;
}
