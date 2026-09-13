namespace RemSound.Core;

/// <summary>
/// Audio format announcement carried in every Format packet. <see cref="Lane"/> was added
/// 2026-05-11 alongside the BothIndependent audio mode — see <see cref="RenderRoute"/> for
/// the semantics. The field is wire-backward-compatible: old receivers parse the first 32
/// bytes of the format payload and ignore the extra; new receivers reading a 32-byte
/// payload from an old sender default Lane to <see cref="RenderRoute.Mixed"/>.
///
/// 2026-05-23 — <c>FrameDurationMilliseconds</c> renamed to <c>FrameSamplesPerChannel</c>
/// (v3.0 wire-format change). Previously held an int millisecond value; now holds the exact
/// sample-count per channel at the announced <see cref="SampleRate"/>. Conversion is
/// <c>ms = FrameSamplesPerChannel * 1000 / SampleRate</c> for display. The change exists
/// because Opus's 2.5 ms RESTRICTED_LOWDELAY frame (= 120 samples at 48 kHz) can't be
/// expressed cleanly in integer milliseconds; using sample-count also removes a lossy
/// conversion that the encoder/decoder pipeline previously did at every announcement.
/// v2.x receivers reading this field will misinterpret the value as milliseconds and
/// over-size their internal buffers; the actual decode still works because the Opus
/// decoder is self-describing from the packet TOC byte. Within-major-version (v3.x ↔ v3.x)
/// the field is unambiguous.
/// </summary>
public sealed record AudioFormatInfo(
    int SampleRate,
    int Channels,
    int BitsPerSample,
    int Encoding,
    int BlockAlign,
    int AverageBytesPerSecond,
    int Codec = (int)AudioTransportCodec.Pcm,
    int FrameSamplesPerChannel = 480,
    RenderRoute Lane = RenderRoute.Mixed,
    /// <summary>The SENDER's own capture latency in milliseconds, carried on the wire so the far end
    /// can report the real journey instead of guessing at the half of it that happens here. Zero means
    /// the sender did not state it (an older build, or a device that will not say), and the receiver
    /// falls back to its own estimate. See RemPacket.FormatPayloadWithCaptureSize. 2026-08-24.</summary>
    double CaptureLatencyMs = 0)
{
    /// <summary>Human-friendly frame duration in milliseconds, derived from
    /// <see cref="FrameSamplesPerChannel"/> and <see cref="SampleRate"/>. May be a fraction
    /// (2.5 ms at 48 kHz / 120 samples). For string formatting only — the encoder/decoder
    /// hot path uses <see cref="FrameSamplesPerChannel"/> directly.</summary>
    public double FrameDurationMs => SampleRate > 0 ? FrameSamplesPerChannel * 1000.0 / SampleRate : 0;

    /// <summary>Largest frame size Opus can encode at 48 kHz (60 ms). Nothing on the wire may
    /// legitimately exceed this, and it is the bound the receiver sizes its decode scratch from.</summary>
    public const int MaxFrameSamplesPerChannel = 2880;

    /// <summary>
    /// Is this format one the receiver can actually decode, or is it garbage?
    ///
    /// <para><b>Why this exists.</b> A Format packet is the one packet that is neither encrypted nor
    /// authenticated — it carries the password fingerprint but is not protected by it — and every
    /// field was read straight off the wire and used with no range check at all. Two concrete
    /// consequences. <c>Channels == 0</c> divided by zero once per packet in the PCM path, caught by
    /// the listener and logged, so the result was a log flood at packet rate. A large
    /// <c>FrameSamplesPerChannel</c> on an Opus stream made the decode scratch
    /// <c>frameSize × channels</c> shorts — a remote-controlled multi-megabyte allocation per packet
    /// on the network thread — before libopus rejected the frame anyway.</para>
    ///
    /// <para>Both need the source to be on the allow-list, which narrows it a lot but does not close
    /// it: UDP source addresses are spoofable on a LAN, and UDP's checksum is 16 bits, so a
    /// corrupted packet from a real peer reaches here too. The rule for anything taking input from
    /// the network is that it fails closed. 2026-08-23 audit, finding R4.</para>
    ///
    /// <para><b>Cross-port safety.</b> This rejects only values no RemSound has ever sent. The
    /// Windows sender emits 48 kHz / 2 ch with a frame size of 120–2880, and the wire LAYOUT is
    /// untouched, so nothing another port sends can start failing because of a change here. Fields
    /// the receiver does not act on — BitsPerSample, Encoding, BlockAlign, AverageBytesPerSecond —
    /// are deliberately NOT checked, so a port that fills them differently keeps working.</para>
    /// </summary>
    public bool IsUsable(out string reason)
    {
        reason = "";
        // Channels: the mix bus is stereo and every decode path indexes interleaved frames by this.
        // Zero is a divide-by-zero; more than two mis-indexes. Opus supports 1 or 2.
        if (Channels is < 1 or > 2)
        {
            reason = $"channel count {Channels} (must be 1 or 2)";
            return false;
        }
        // Sample rate: a sane audio range at minimum, so the frame-duration and buffer maths cannot
        // be driven silly. Deliberately NOT pinned to 48 kHz — that would be a policy change for a
        // port that may one day send something else.
        if (SampleRate is < 8000 or > 192000)
        {
            reason = $"sample rate {SampleRate} (outside 8000-192000)";
            return false;
        }
        // Frame size: the number the decode scratch is sized from. Cap at Opus's own maximum.
        if (FrameSamplesPerChannel is < 1 or > MaxFrameSamplesPerChannel)
        {
            reason = $"frame size {FrameSamplesPerChannel} samples (must be 1-{MaxFrameSamplesPerChannel})";
            return false;
        }
        // Codec: only the two we can decode. An unknown value already produced no audio — it just
        // did so after opening a session and then dropping every packet.
        if (Codec is not ((int)AudioTransportCodec.Pcm or (int)AudioTransportCodec.Opus))
        {
            reason = $"unknown codec {Codec}";
            return false;
        }
        // Opus is stricter than the general case: libopus accepts only these rates and its decoder
        // constructor throws on anything else. Fail here rather than in a constructor.
        if (Codec == (int)AudioTransportCodec.Opus
            && SampleRate is not (8000 or 12000 or 16000 or 24000 or 48000))
        {
            reason = $"Opus at {SampleRate} Hz (Opus supports 8000, 12000, 16000, 24000 or 48000)";
            return false;
        }
        return true;
    }

    public override string ToString()
    {
        var encodingName = Encoding switch
        {
            1 => "PCM",
            3 => "IEEE float",
            _ => $"encoding {Encoding}"
        };
        var codecName = (AudioTransportCodec)Codec switch
        {
            AudioTransportCodec.Opus => $" over Opus ({FrameDurationMs:0.##} ms)",
            _ => ""
        };
        var laneName = Lane == RenderRoute.Mixed ? "" : $" [{Lane}]";
        return $"{SampleRate} Hz, {Channels} channel(s), {BitsPerSample}-bit {encodingName}{codecName}{laneName}";
    }
}
