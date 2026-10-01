namespace RemSound.Core;

/// <summary>
/// JAMULUS-STYLE OPUS, AUTOMATIC. Opus in the very short frames Jamulus uses - 64 samples, 1.33 ms - which standard Opus
/// cannot make, through our own libopus built with custom modes (remsound-opus.dll, <see cref="OpusCustomNative"/>).
///
/// <para>Ed's rule (2026-09-26, restated 2026-09-28): "64 samples whenever both ends are on ASIO, standard Opus
/// otherwise". So the ASIO lane of one copy sends it, in place of Opus, to every copy that plays through ASIO and says
/// so; everyone else - phones, a copy listening on WASAPI only, an older RemSound - keeps the normal stream, and a PCM
/// choice stays PCM. A copy says so in every Format packet it sends and in the tenth byte of every heartbeat, so one that
/// only listens is asked too. Through a server it goes only while everyone the server passes it to can play it
/// (AudioSender.IsCustomOpusTarget). A missing frame is made up by Opus rather than left silent.</para>
///
/// <para>Until 2026-10-01 this was a switch on the Latency lab tab of Preferences, the last thing left of the lab (Ed:
/// "latency lab tab is still there with opus switch but that is supposed to be automatic now. so that needs coming
/// out"). It waited on Andre's Mac app, which dropped a heartbeat longer than nine bytes until its 0.1.44.</para>
/// </summary>
public static class JamulusStyle
{
    /// <summary>The frame size the app sends: 64 samples.</summary>
    public const int DefaultFrameSamples = 64;

    /// <summary>The frame size the ASIO lane sends. Always <see cref="DefaultFrameSamples"/> in the app; the simulator sets
    /// 128 to replay a network recorded while the lab still offered it.</summary>
    public static volatile int FrameSamples = DefaultFrameSamples;

    /// <summary>This copy plays what it receives through ASIO: it is receiving, with an ASIO output ticked (alone or beside
    /// WASAPI ones). The main window keeps it in step with the outputs, once a second; the service, which only sends,
    /// never sets it.</summary>
    public static volatile bool PlaysThroughAsio;

    /// <summary>What this copy tells the others, in its Format packets and heartbeats: it can play Jamulus-style frames -
    /// it plays through ASIO and the library loaded.</summary>
    public static bool CanTakeIt => PlaysThroughAsio && OpusCustomNative.Available;

    /// <summary>One line for the log.</summary>
    public static string Describe() =>
        CanTakeIt ? "this computer plays through ASIO, so it asks others for Jamulus-style frames"
        : PlaysThroughAsio ? $"this computer plays through ASIO but cannot decode Jamulus-style frames ({OpusCustomNative.LoadMessage})"
        : "this computer does not play through ASIO, so it asks nobody for Jamulus-style frames";

    /// <summary>Back to how a fresh process starts (tests, and the simulator).</summary>
    public static void Reset()
    {
        PlaysThroughAsio = false;
        FrameSamples = DefaultFrameSamples;
    }
}
