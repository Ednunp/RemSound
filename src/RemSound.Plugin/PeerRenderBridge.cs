using NAudio.Dsp;
using RemSound.Core;

namespace RemSound.Plugin;

/// <summary>
/// The receive direction: a peer's audio, from the app, onto this DAW track.
///
/// <para>The mirror image of <see cref="HostCaptureBackend"/> and it inherits the same two rules —
/// nothing allocates on the audio thread, and the host is not necessarily running at 48 kHz. If the
/// rate conversion is skipped or done the wrong way round, the peer arrives transposed; that is
/// SonoBus's open issue #1, and it is cheap to avoid on day one.</para>
///
/// <para><b>How many frames to ask for.</b> The DAW wants <c>n</c> frames at its rate, which is
/// <c>n × 48000 / hostRate</c> frames on the wire. Asking for the host count at 44.1 kHz would starve
/// the track by about 9%, which sounds like a steady stream of tiny dropouts rather than an obvious
/// fault — the kind of thing that gets blamed on the network for a week.</para>
/// </summary>
internal sealed class PeerRenderBridge
{
    private const int Channels = PluginBridgeProtocol.WireChannels;

    private readonly PluginBridgeClient client;
    private readonly WdlResampler resampler = new();

    private float[] wireIn = [];
    private float[] hostOut = [];
    private double hostSampleRate = PluginBridgeProtocol.WireSampleRate;

    // Resampled host-rate frames produced but not yet handed to the DAW.
    //
    // Why a carry is needed at all: the number of host frames a resampler returns for a given number
    // of wire frames is not exact. Converting 48 kHz down to 44.1 kHz, one block's worth of wire
    // audio comes back as hostFrames or hostFrames-1 depending on where the phase happens to sit.
    // The old code cleared the block, wrote `produced` frames and left the remainder silent — so on
    // the short blocks the DAW got a zeroed sample at the end, repeating at block rate. That is an
    // audible tick, and one that would be blamed on the network rather than on arithmetic. Carrying
    // the surplus into the next block makes the output continuous and self-correcting: ask for
    // slightly more than the block needs, emit exactly the block, keep the rest.
    // Only used on the resampling path — at 48 kHz the conversion is 1:1 and exact.
    // 2026-08-23 audit, finding P2.
    private float[] carry = [];
    private int carryFrames;

    public PeerRenderBridge(PluginBridgeClient client)
    {
        this.client = client;
        resampler.SetMode(true, 0, false);
        resampler.SetFeedMode(true);
    }

    /// <summary>Allocate everything the audio thread will touch, off the audio thread.</summary>
    public void PrepareForBlockSize(int maxFramesPerBlock, double sampleRate)
    {
        hostSampleRate = sampleRate <= 0 ? PluginBridgeProtocol.WireSampleRate : sampleRate;
        resampler.SetRates(PluginBridgeProtocol.WireSampleRate, hostSampleRate);

        var hostFloats = Math.Max(1, maxFramesPerBlock) * Channels;
        // Wire frames can exceed host frames when the host runs BELOW 48 kHz (a 44.1k host needs
        // ~1.09 wire frames per host frame). Doubled for headroom; a few hundred kilobytes, once.
        var wireFloats = (int)Math.Ceiling(hostFloats * (PluginBridgeProtocol.WireSampleRate / Math.Max(1.0, hostSampleRate)) * 2) + Channels * 8;
        if (wireIn.Length < wireFloats) wireIn = new float[wireFloats];
        if (hostOut.Length < hostFloats + Channels * 8) hostOut = new float[hostFloats + Channels * 8];
        // The carry holds at most one block plus the rounding surplus. Doubled so a block that
        // produces a little over never has to reallocate on the audio thread.
        if (carry.Length < hostFloats * 2 + Channels * 8) carry = new float[hostFloats * 2 + Channels * 8];
        // A format change invalidates anything held at the old rate.
        carryFrames = 0;
    }

    /// <summary>Fill one DAW block from the claimed peer. Audio thread; allocation-free.</summary>
    /// <param name="mixInto">Sum the peer on top of whatever the caller has already put in the block,
    /// rather than replacing it. That is the normal case for an FX insert: the track's own audio has
    /// to survive the plugin, or putting RemSound on a track that is also playing something silences
    /// it (Anthony Reyers, 2026-08-28). Replacing is kept available for a bus that carries nothing but
    /// the peer, where clearing first saves a pointless add.</param>
    /// <param name="gain">Receive trim, 1 = unity. Applied as the peer is written or summed, so it
    /// touches only their audio and never the track's own.</param>
    /// <returns>Frames actually filled from the peer — short while the buffer is still building, which
    /// the caller leaves alone rather than repeating stale audio.</returns>
    public int FillHostBlock(Span<double> left, Span<double> right, float gain = 1f, bool mixInto = false)
    {
        var hostFrames = Math.Min(left.Length, right.Length);
        if (hostFrames <= 0) return 0;
        if (!mixInto)
        {
            left[..hostFrames].Clear();
            right[..hostFrames].Clear();
        }

        // Fast path: the host is already at our rate, so there is no rounding to absorb.
        if (Math.Abs(hostSampleRate - PluginBridgeProtocol.WireSampleRate) < 0.5)
        {
            if (wireIn.Length < hostFrames * Channels) return 0;
            var direct = client.ReadPeerBlock(wireIn.AsSpan(0, hostFrames * Channels), hostFrames);
            if (direct <= 0) return 0;
            if (mixInto)
            {
                for (var i = 0; i < direct; i++)
                {
                    left[i] += wireIn[i * Channels] * gain;
                    right[i] += wireIn[i * Channels + 1] * gain;
                }
            }
            else
            {
                for (var i = 0; i < direct; i++)
                {
                    left[i] = wireIn[i * Channels] * gain;
                    right[i] = wireIn[i * Channels + 1] * gain;
                }
            }
            return direct;
        }

        // Resampling path. Top the carry up to at least a full block before emitting, so a block is
        // never one frame short of what the DAW asked for. See the `carry` field.
        if (carryFrames < hostFrames)
        {
            // Ask for the shortfall plus a small pad, so rounding lands ABOVE the requirement and the
            // surplus rolls into the next block rather than the deficit rolling into this one.
            const int PadFrames = 2;
            var hostShortfall = hostFrames - carryFrames + PadFrames;
            var wireFrames = (int)Math.Ceiling(hostShortfall * PluginBridgeProtocol.WireSampleRate / Math.Max(1.0, hostSampleRate));
            if (wireIn.Length >= wireFrames * Channels)
            {
                var got = client.ReadPeerBlock(wireIn.AsSpan(0, wireFrames * Channels), wireFrames);
                if (got > 0)
                {
                    var needed = resampler.ResamplePrepare(got, Channels, out var inBuf, out var inOffset);
                    var copy = Math.Min(needed, got);
                    for (var i = 0; i < copy * Channels; i++) inBuf[inOffset + i] = wireIn[i];
                    var room = Math.Min(hostOut.Length / Channels, (carry.Length / Channels) - carryFrames);
                    var produced = room > 0 ? resampler.ResampleOut(hostOut, 0, copy, room, Channels) : 0;
                    if (produced > 0)
                    {
                        Array.Copy(hostOut, 0, carry, carryFrames * Channels, produced * Channels);
                        carryFrames += produced;
                    }
                }
            }
        }

        // Emit whatever the carry can cover — a full block in steady state, less only when the app
        // is genuinely starving us, which is left as silence rather than repeated audio.
        var emit = Math.Min(hostFrames, carryFrames);
        if (mixInto)
        {
            for (var i = 0; i < emit; i++)
            {
                left[i] += carry[i * Channels] * gain;
                right[i] += carry[i * Channels + 1] * gain;
            }
        }
        else
        {
            for (var i = 0; i < emit; i++)
            {
                left[i] = carry[i * Channels] * gain;
                right[i] = carry[i * Channels + 1] * gain;
            }
        }
        // Shift the surplus down for next time.
        var remaining = carryFrames - emit;
        if (remaining > 0) Array.Copy(carry, emit * Channels, carry, 0, remaining * Channels);
        carryFrames = remaining;
        return emit;
    }
}
