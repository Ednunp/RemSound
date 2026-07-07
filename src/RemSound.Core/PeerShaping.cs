namespace RemSound.Core;

/// <summary>Which EQ the user is shaping a peer with. The three modes are INDEPENDENT: switching
/// between them keeps each mode's own settings — nothing is carried over. The active mode is the
/// one that's applied to the sound.</summary>
public enum PeerEqMode
{
    /// <summary>3-band tone control (bass / mids / treble). Display name: "3 band simple EQ".</summary>
    Simple3Band = 0,
    /// <summary>12-band graphic EQ with fixed centre frequencies. Display name: "12 band advanced
    /// graphic EQ". (The enum member keeps its historic name; the numeric value is what's persisted.)</summary>
    Advanced10Band = 1,
    /// <summary>Up to 16 user-defined parametric bands, each a boost/cut across a start→end frequency
    /// range. Display name: "16 band parametric EQ".</summary>
    Parametric16Band = 2,
}

/// <summary>One user-defined parametric EQ band: a boost or cut spread across the range from
/// <see cref="StartHz"/> to <see cref="EndHz"/>. Realised as a peaking filter centred on the
/// geometric mean of the two frequencies, widened to span the range (see
/// <see cref="PeerEqBands.ParametricToPeaking"/>).</summary>
public sealed class ParametricBand
{
    public float StartHz { get; set; }
    public float EndHz { get; set; }
    /// <summary>Gain in dB, -12..+12.</summary>
    public float GainDb { get; set; }

    public ParametricBand Clone() => new() { StartHz = StartHz, EndHz = EndHz, GainDb = GainDb };
}

/// <summary>Per-peer volume + pan + EQ, saved per profile (see <see cref="Profile.PeerShaping"/>). Keyed in
/// the profile by the same peer-entry string as <see cref="Profile.SelectedConnectedPeers"/>.</summary>
public sealed class PeerShaping
{
    /// <summary>Whether this peer is shaped at all. When false the peer passes through untouched even
    /// while the profile-wide master switch is on — a per-peer bypass that keeps the settings below.
    /// Defaults to true so a freshly-seen peer is covered the moment the master switch is turned on.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>-1 (full left) .. 0 (centre) .. +1 (full right).</summary>
    public float Pan { get; set; }

    /// <summary>Per-peer playback level, 0..1 (1 = 100%, unity). An individual fader for this peer,
    /// on top of the global volume. 100% is transparent.</summary>
    public float Volume { get; set; } = 1f;

    /// <summary>Which EQ mode is currently active for this peer.</summary>
    public PeerEqMode EqMode { get; set; } = PeerEqMode.Simple3Band;

    /// <summary>Gains in dB (-12..+12) for the 3 simple bands, in the order of <see cref="PeerEqBands.Simple"/>
    /// (bass, mids, treble). Kept independently of the other modes.</summary>
    public float[] SimpleBandsDb { get; set; } = new float[3];

    /// <summary>Gains in dB (-12..+12) for the 12 advanced graphic-EQ bands, in the order of
    /// <see cref="PeerEqBands.Advanced"/>. Kept independently of the other modes.</summary>
    public float[] AdvancedBandsDb { get; set; } = new float[12];

    /// <summary>The user's parametric bands (up to <see cref="PeerEqBands.ParametricMaxBands"/>). Kept
    /// independently of the two graphic modes. Empty = flat.</summary>
    public List<ParametricBand> ParametricBands { get; set; } = new();
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

    /// <summary>Most parametric bands a peer can hold.</summary>
    public const int ParametricMaxBands = 16;

    /// <summary>Lowest / highest frequency a parametric band edge can be set to.</summary>
    public const float ParametricMinHz = 20f;
    public const float ParametricMaxHz = 20000f;

    /// <summary>3-band "tone control": bass (low shelf), mids (peaking), treble (high shelf).</summary>
    public static readonly (string Label, double Freq)[] Simple =
    [
        ("Bass", 100.0),
        ("Mids", 1000.0),
        ("Treble", 8000.0),
    ];

    /// <summary>12-band graphic EQ — 80 Hz added in the low end, finer through the presence region
    /// (2/4/6/8 kHz) so there's a band between 4 and 8 kHz. All peaking.</summary>
    public static readonly (string Label, double Freq)[] Advanced =
    [
        ("31 Hz", 31.0),
        ("63 Hz", 63.0),
        ("80 Hz", 80.0),
        ("125 Hz", 125.0),
        ("250 Hz", 250.0),
        ("500 Hz", 500.0),
        ("1 kHz", 1000.0),
        ("2 kHz", 2000.0),
        ("4 kHz", 4000.0),
        ("6 kHz", 6000.0),
        ("8 kHz", 8000.0),
        ("16 kHz", 16000.0),
    ];

    /// <summary>Converts a parametric band's start/end frequency range into a peaking-filter centre
    /// frequency and Q. Centre is the geometric mean of the two edges; Q comes from the range's width
    /// in octaves (the standard RBJ bandwidth-to-Q relation). Both the DSP and the on-screen response
    /// curve call this so they always agree.</summary>
    public static void ParametricToPeaking(float startHz, float endHz, out float centerHz, out float q)
    {
        startHz = Math.Clamp(startHz, ParametricMinHz, ParametricMaxHz);
        endHz = Math.Clamp(endHz, ParametricMinHz, ParametricMaxHz);
        if (endHz <= startHz) endHz = MathF.Min(startHz * 1.01f, ParametricMaxHz);
        centerHz = MathF.Sqrt(startHz * endHz);
        float bwOct = MathF.Log2(endHz / startHz);
        if (bwOct < 0.01f) bwOct = 0.01f;
        float twoPowBw = MathF.Pow(2f, bwOct);
        q = MathF.Sqrt(twoPowBw) / (twoPowBw - 1f);
        q = Math.Clamp(q, 0.1f, 12f);
    }
}
