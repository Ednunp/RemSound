namespace RemSound.Core;

/// <summary>
/// Transport rules shared by the main window and the send-only service. Moved here from MainForm
/// (review sweep): the headless service reaching into a WinForms Form for a frame-size rule was an
/// inverted dependency, and the service's fixed audio numbers were hard-coded in two files that had
/// to agree by luck.
/// </summary>
public static class AudioTransportRules
{
    /// <summary>Maps the codec + configured Opus frame + send-rate to the frame the encoder actually
    /// uses, in samples-per-channel at 48 kHz. Standard returns the codec's natural frame; Tight halves
    /// it, floored at 120 samples = 2.5 ms (libopus RESTRICTED_LOWDELAY minimum). PCM frame size is set
    /// separately in AudioSender.SetSendRate.</summary>
    public static int EffectiveOpusFrameSamples(AudioTransportCodec codec, int opusFrameSamples, SendRate rate)
    {
        if (codec != AudioTransportCodec.Opus) return opusFrameSamples;
        return rate == SendRate.Tight ? Math.Max(120, opusFrameSamples / 2) : opusFrameSamples;
    }
}

/// <summary>
/// The service's FIXED audio transport — the known-good live-jamming config (raw PCM sounded hideous
/// over the service; Opus at the 2.5 ms frame with Small packets and lock-to-clock is what sounds
/// right — Ed, 2026-07-17). The config dialog writes these into the saved service profile AND the
/// service host re-forces them at runtime; both reference THIS so they can never disagree.
/// </summary>
public static class ServiceAudioDefaults
{
    public const AudioTransportCodec Codec = AudioTransportCodec.Opus;
    public const int OpusFrameSamplesPerChannel = 120; // 2.5 ms at 48 kHz
    public const SendRate Rate = SendRate.Tight;       // "Small" packets
}
