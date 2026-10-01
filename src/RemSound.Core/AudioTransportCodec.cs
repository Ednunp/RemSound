namespace RemSound.Core;

public enum AudioTransportCodec
{
    Pcm = 1,
    Opus = 2,
    /// <summary>Jamulus-style frames (2026-09-26, automatic since 2026-10-01: <see cref="JamulusStyle"/>): Opus custom-mode
    /// CELT at 64/128/256 samples, from remsound-opus.dll. NOT standard Opus - sent only to a RemSound that has said it can
    /// decode it (see
    /// <see cref="AudioFormatInfo.LabFlags"/>). Phones, older copies and the server never see a stream with this codec.</summary>
    OpusCustom = 3,
}
