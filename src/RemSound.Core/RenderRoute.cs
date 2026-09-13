namespace RemSound.Core;

/// <summary>
/// Tag carried in the per-stream <see cref="AudioFormatInfo.Lane"/> wire field, telling the
/// receiver which render backend a particular stream's audio belongs to.
///
/// <see cref="Mixed"/> is current, not legacy. A sender with no ASIO driver chosen
/// (<see cref="AudioMode.WasapiOnly"/>) sends its capture on it, a sender with an ASIO driver sends its
/// DAW plugin lane on it (its two capture lanes already hold the other two values), and it is what
/// every sender from before 2026-05-11 means. The receiver's <c>PlayoutEngine</c> plays a Mixed
/// session on exactly one active lane — the WASAPI lane by preference — so a plain stream is heard
/// whatever the receiver's own mode.
///
/// <see cref="AudioMode.BothIndependent"/> (added 2026-05-11) is the reason the other two values
/// exist: in that mode the sender emits *two* capture streams in parallel — a WASAPI lane at WASAPI's
/// native latency and an ASIO lane at ASIO's native latency. The sender tags each lane with
/// <see cref="WasapiLane"/> or <see cref="AsioLane"/>; the receiver routes each lane's audio to a
/// separate <c>SessionPlayout</c> group, and each render backend reads only the group it owns. No
/// cross-clock resampler, no tee — each lane stays at its own native latency end-to-end.
///
/// Wire format: stored as a single byte at offset 32 of the format payload. Receivers that
/// don't understand the field (pre-2026-05-11 builds) parse only the first 32 bytes and
/// behave exactly as before — the new field is purely additive. Receivers that do understand
/// it but receive a 32-byte payload (because the sender is old) default to <see cref="Mixed"/>,
/// and so does an unknown value. The numbers are on the wire and other RemSound ports read them:
/// never renumber.
/// </summary>
public enum RenderRoute : byte
{
    /// <summary>Mixed with every other Mixed stream and played on one active lane (see the type
    /// summary). Sent by a WASAPI-only sender's capture and by the DAW plugin lane when an ASIO driver
    /// is chosen, and the value read when the format packet has no Lane field or an unknown one.</summary>
    Mixed = 0,

    /// <summary>Stream belongs to the WASAPI render lane and should only reach WASAPI output
    /// devices, bypassing the cross-backend mix. Emitted by the WASAPI capture lane of a sender with
    /// an ASIO driver chosen (BothIndependent).</summary>
    WasapiLane = 1,

    /// <summary>Stream belongs to the ASIO render lane and should only reach ASIO outputs,
    /// bypassing the cross-backend mix. Emitted by the ASIO capture lane in BothIndependent, and by a
    /// WASAPI-only sender's DAW plugin lane, which takes the one lane byte its capture is not using.</summary>
    AsioLane = 2,
}
