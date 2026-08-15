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
    }

    /// <summary>Fill one DAW block from the claimed peer. Audio thread; allocation-free.</summary>
    /// <returns>Frames actually filled — short while the buffer is still building, which the caller
    /// leaves as silence rather than repeating stale audio.</returns>
    public int FillHostBlock(Span<double> left, Span<double> right)
    {
        var hostFrames = Math.Min(left.Length, right.Length);
        if (hostFrames <= 0) return 0;
        left[..hostFrames].Clear();
        right[..hostFrames].Clear();

        var wireFrames = (int)Math.Ceiling(hostFrames * PluginBridgeProtocol.WireSampleRate / Math.Max(1.0, hostSampleRate));
        if (wireIn.Length < wireFrames * Channels) return 0;

        var got = client.ReadPeerBlock(wireIn.AsSpan(0, wireFrames * Channels), wireFrames);
        if (got <= 0) return 0;

        // Fast path: the host is already at our rate.
        if (Math.Abs(hostSampleRate - PluginBridgeProtocol.WireSampleRate) < 0.5)
        {
            var direct = Math.Min(got, hostFrames);
            for (var i = 0; i < direct; i++)
            {
                left[i] = wireIn[i * Channels];
                right[i] = wireIn[i * Channels + 1];
            }
            return direct;
        }

        var needed = resampler.ResamplePrepare(got, Channels, out var inBuf, out var inOffset);
        var copy = Math.Min(needed, got);
        for (var i = 0; i < copy * Channels; i++) inBuf[inOffset + i] = wireIn[i];
        var produced = resampler.ResampleOut(hostOut, 0, copy, Math.Min(hostFrames, hostOut.Length / Channels), Channels);

        for (var i = 0; i < produced; i++)
        {
            left[i] = hostOut[i * Channels];
            right[i] = hostOut[i * Channels + 1];
        }
        return produced;
    }
}
