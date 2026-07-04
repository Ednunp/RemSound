using NAudio.Dsp;
using RemSound.Core;

namespace RemSound.Receiver;

/// <summary>
/// Per-peer pan + EQ, applied to that one peer's decoded stereo block just before it is summed into
/// the mix (see <see cref="SessionPlayout"/>.ReadFloats). Immutable once built: when the user changes
/// a setting the UI thread builds a fresh <see cref="PeerDspChain"/> and swaps the reference on the
/// SessionPlayout in a single assignment; the audio thread reads that reference once per block. No
/// locks and no allocation on the audio thread — the same build-new-and-swap idiom RemSound already
/// uses for its other audio-thread parameters. Every operation is per-sample (a balance-style pan
/// plus RBJ biquad IIR EQ), so it adds ZERO buffering latency to the mix — only CPU, and very little.
/// </summary>
public sealed class PeerDspChain
{
    private readonly float panL;
    private readonly float panR;
    private readonly bool hasPan;
    // Same coefficients on both channels, but each needs its own filter instance because a biquad
    // carries per-channel state. left.Length == right.Length always.
    private readonly BiQuadFilter[] left;
    private readonly BiQuadFilter[] right;

    private PeerDspChain(float panL, float panR, bool hasPan, BiQuadFilter[] left, BiQuadFilter[] right)
    {
        this.panL = panL;
        this.panR = panR;
        this.hasPan = hasPan;
        this.left = left;
        this.right = right;
    }

    /// <summary>True when this chain would do nothing (pan off/centre and EQ off/flat). Build returns
    /// null in that case so an unshaped peer's <c>dsp</c> reference is null and it pays nothing.</summary>
    public bool IsNoOp => !hasPan && left.Length == 0;

    /// <summary>Builds a chain for one peer from its saved shaping and the profile's two master
    /// switches. Returns null if there's nothing to do — pan disabled or centred, and EQ disabled or
    /// completely flat. Runs on the UI thread; the result is swapped onto the audio thread atomically.</summary>
    public static PeerDspChain? Build(PeerShaping? shaping, bool applyPan, bool applyEq)
    {
        // Pan is a balance control: it keeps the peer's stereo image (never sums to mono). Centre is
        // unity on both sides; panning toward one side attenuates the OPPOSITE channel, reaching zero
        // at the extreme. So a stereo signal just leans left or right rather than collapsing.
        float pan = shaping is null ? 0f : Math.Clamp(shaping.Pan, -1f, 1f);
        bool hasPan = applyPan && pan != 0f;
        float panL = pan > 0f ? 1f - pan : 1f;
        float panR = pan < 0f ? 1f + pan : 1f;

        var l = new List<BiQuadFilter>();
        var r = new List<BiQuadFilter>();
        if (applyEq && shaping is not null)
        {
            var mode = shaping.EqMode;
            var bands = mode == PeerEqMode.Advanced10Band ? PeerEqBands.Advanced : PeerEqBands.Simple;
            var gains = mode == PeerEqMode.Advanced10Band ? shaping.AdvancedBandsDb : shaping.SimpleBandsDb;
            for (int i = 0; i < bands.Length; i++)
            {
                float gainDb = gains is not null && i < gains.Length ? gains[i] : 0f;
                if (MathF.Abs(gainDb) < 0.05f) continue;   // flat band — no filter needed, skip it
                l.Add(MakeBand(mode, i, bands.Length, (float)bands[i].Freq, gainDb));
                r.Add(MakeBand(mode, i, bands.Length, (float)bands[i].Freq, gainDb));
            }
        }

        var chain = new PeerDspChain(panL, panR, hasPan, [.. l], [.. r]);
        return chain.IsNoOp ? null : chain;
    }

    private static BiQuadFilter MakeBand(PeerEqMode mode, int index, int count, float freq, float gainDb)
    {
        // 3-band tone control: bass is a low shelf, treble a high shelf, mids a peaking band — the
        // natural shape for a simple bass/mid/treble control. 10-band graphic EQ: peaking throughout,
        // with a Q suited to roughly one-octave band spacing.
        if (mode == PeerEqMode.Simple3Band && index == 0)
            return BiQuadFilter.LowShelf(PeerEqBands.MixSampleRate, freq, 0.7f, gainDb);
        if (mode == PeerEqMode.Simple3Band && index == count - 1)
            return BiQuadFilter.HighShelf(PeerEqBands.MixSampleRate, freq, 0.7f, gainDb);
        return BiQuadFilter.PeakingEQ(PeerEqBands.MixSampleRate, freq, mode == PeerEqMode.Advanced10Band ? 1.4f : 0.9f, gainDb);
    }

    /// <summary>Process one interleaved stereo block IN PLACE. <paramref name="frames"/> is the number
    /// of stereo frames (the used span length is frames*2). Per-sample, no allocation, no locks —
    /// safe to call on the audio render thread.</summary>
    public void Process(Span<float> output, int frames)
    {
        int n = left.Length;
        if (n > 0)
        {
            for (int f = 0; f < frames; f++)
            {
                float sl = output[2 * f];
                float sr = output[2 * f + 1];
                for (int b = 0; b < n; b++)
                {
                    sl = left[b].Transform(sl);
                    sr = right[b].Transform(sr);
                }
                output[2 * f] = sl;
                output[2 * f + 1] = sr;
            }
        }
        if (hasPan)
        {
            for (int f = 0; f < frames; f++)
            {
                output[2 * f] *= panL;
                output[2 * f + 1] *= panR;
            }
        }
    }
}
