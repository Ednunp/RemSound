namespace RemSound.Core;

/// <summary>Which EQ the user is shaping a peer with. The two modes are INDEPENDENT: switching
/// between them keeps each mode's own band gains — nothing is carried over. The active mode is the
/// one that's applied to the sound.</summary>
public enum PeerEqMode
{
    Simple3Band = 0,
    Advanced10Band = 1,
}

/// <summary>Per-peer pan + EQ, saved per profile (see <see cref="Profile.PeerShaping"/>). Keyed in
/// the profile by the same peer-entry string as <see cref="Profile.SelectedConnectedPeers"/>.</summary>
public sealed class PeerShaping
{
    /// <summary>-1 (full left) .. 0 (centre) .. +1 (full right).</summary>
    public float Pan { get; set; }

    /// <summary>Which EQ mode is currently active for this peer.</summary>
    public PeerEqMode EqMode { get; set; } = PeerEqMode.Simple3Band;

    /// <summary>Gains in dB (-12..+12) for the 3 simple bands, in the order of <see cref="PeerEqBands.Simple"/>
    /// (bass, mids, treble). Kept independently of the advanced bands.</summary>
    public float[] SimpleBandsDb { get; set; } = new float[3];

    /// <summary>Gains in dB (-12..+12) for the 10 advanced graphic-EQ bands, in the order of
    /// <see cref="PeerEqBands.Advanced"/>. Kept independently of the simple bands.</summary>
    public float[] AdvancedBandsDb { get; set; } = new float[10];
}

/// <summary>The fixed EQ band layouts. The user sets each band's GAIN; the centre frequencies are
/// fixed so the controls stay simple and predictable. Shared by the DSP (which builds the filters)
/// and the UI (which labels the sliders), so the two can never drift apart.</summary>
public static class PeerEqBands
{
    /// <summary>RemSound's internal mix sample rate. All received audio is resampled to this stereo
    /// float mix before per-peer shaping, so filter coefficients are computed against it.</summary>
    public const int MixSampleRate = 48000;

    /// <summary>Maximum boost/cut for any band, in dB. Sliders run -12..+12.</summary>
    public const float MaxGainDb = 12f;

    /// <summary>3-band "tone control": bass (low shelf), mids (peaking), treble (high shelf).</summary>
    public static readonly (string Label, double Freq)[] Simple =
    [
        ("Bass", 100.0),
        ("Mids", 1000.0),
        ("Treble", 8000.0),
    ];

    /// <summary>10-band graphic EQ on ISO octave centres (all peaking).</summary>
    public static readonly (string Label, double Freq)[] Advanced =
    [
        ("31 Hz", 31.0),
        ("63 Hz", 63.0),
        ("125 Hz", 125.0),
        ("250 Hz", 250.0),
        ("500 Hz", 500.0),
        ("1 kHz", 1000.0),
        ("2 kHz", 2000.0),
        ("4 kHz", 4000.0),
        ("8 kHz", 8000.0),
        ("16 kHz", 16000.0),
    ];
}
