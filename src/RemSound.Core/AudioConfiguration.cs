namespace RemSound.Core;

/// <summary>
/// THE THREE AUDIO CONFIGURATIONS. There are three, not two, and this type exists so that stops
/// being something anyone has to remember.
///
/// <para><b>Why it exists.</b> RemSound has three real configurations — WASAPI only, ASIO only, and
/// both at once — and they are decided by WHICH OUTPUT DEVICES ARE TICKED. But the obvious thing to
/// branch on in the code was <see cref="AudioMode"/>, which has two useful values and really only
/// answers "is an ASIO driver chosen at all". That is a different question, and reaching for it
/// silently collapses ASIO-only into the same branch as both-lanes.</para>
///
/// <para>That is not a hypothetical. It produced a jitter-buffer label that told an ASIO-only user
/// about a WASAPI control they were not using, and a device-latency figure that answered one lane
/// with the other lane's number in the very configuration Ed runs in. Ed has had to point out the
/// three configurations repeatedly, and his verdict on the second time was fair: "why can you not
/// get this right in your head ... you should never ever need me to correct you on this again."</para>
///
/// <para><b>So the fix is structural, not advisory.</b> A note in a document is something to
/// remember; a three-valued type is something the compiler hands you. Switch over it and the
/// analyser tells you when a case is missing. Loop over <see cref="AudioConfigurations.All"/> and a
/// test covers three by construction rather than by diligence. And
/// <c>SelfTest.AudioConfigurationCoverage</c> fails the build when a gate step touches this axis and
/// does neither. 2026-08-24.</para>
/// </summary>
public enum AudioConfiguration
{
    /// <summary>WASAPI output devices ticked, no ASIO ones. Also the shape the app is in before any
    /// output is ticked at all — there is one lane and it is the WASAPI one.</summary>
    WasapiOnly,

    /// <summary>ASIO output ticked and no WASAPI output. A REAL configuration, and the one that gets
    /// forgotten: an ASIO driver is chosen, so the mode flag says "BothIndependent", but only one
    /// lane is actually carrying audio and the WASAPI controls govern nothing.</summary>
    AsioOnly,

    /// <summary>Both kinds of output ticked. Two lanes running at the same time, each with its own
    /// jitter buffer, its own device and its own latency — Ed's normal working setup, because he
    /// switches between them.</summary>
    Both,
}

/// <summary>Helpers for <see cref="AudioConfiguration"/>. The canonical list lives here so no test
/// has to write its own and get it short.</summary>
public static class AudioConfigurations
{
    /// <summary>All three, in a fixed order. THE list — a test that loops over this covers every
    /// configuration by construction. Anything that enumerates configurations by hand is what the
    /// coverage guard is looking for.</summary>
    public static readonly IReadOnlyList<AudioConfiguration> All =
    [
        AudioConfiguration.WasapiOnly,
        AudioConfiguration.AsioOnly,
        AudioConfiguration.Both,
    ];

    /// <summary>
    /// Work out the configuration from what is actually ticked. This — not the audio-mode setting —
    /// is the question that matters.
    /// </summary>
    /// <param name="wasapiOutputTicked">At least one WASAPI output device is selected.</param>
    /// <param name="asioOutputTicked">At least one ASIO output pair is selected AND an ASIO driver
    /// is chosen. Ticking an ASIO pair with no driver is not a configuration, it is a stale setting.</param>
    public static AudioConfiguration From(bool wasapiOutputTicked, bool asioOutputTicked)
    {
        if (wasapiOutputTicked && asioOutputTicked) return AudioConfiguration.Both;
        if (asioOutputTicked) return AudioConfiguration.AsioOnly;
        // Nothing ticked at all collapses here deliberately. It is not a fourth configuration: there
        // is no ASIO lane, nothing is being rendered, and every control behaves as it does in the
        // single-lane WASAPI world. Naming it separately would add a case every caller has to handle
        // for a state in which no audio is playing.
        return AudioConfiguration.WasapiOnly;
    }

    /// <summary>Is the WASAPI lane carrying audio in this configuration?</summary>
    public static bool UsesWasapi(this AudioConfiguration configuration) =>
        configuration is AudioConfiguration.WasapiOnly or AudioConfiguration.Both;

    /// <summary>Is the ASIO lane carrying audio in this configuration?</summary>
    public static bool UsesAsio(this AudioConfiguration configuration) =>
        configuration is AudioConfiguration.AsioOnly or AudioConfiguration.Both;

    /// <summary>True when TWO lanes are live and must therefore be reported apart. The single
    /// question worth asking before naming a lane in a label or a figure: with one lane there is
    /// nothing to distinguish, and naming it is noise; with two, blending them hides the difference.</summary>
    public static bool HasTwoLanes(this AudioConfiguration configuration) =>
        configuration == AudioConfiguration.Both;

    /// <summary>The render route a session lands on in this configuration. Both-lanes has two, so it
    /// has no single answer and callers must ask per lane — which is the point.</summary>
    public static RenderRoute? SingleRoute(this AudioConfiguration configuration) => configuration switch
    {
        AudioConfiguration.WasapiOnly => RenderRoute.WasapiLane,
        AudioConfiguration.AsioOnly => RenderRoute.AsioLane,
        _ => null,
    };

    /// <summary>Human name, for log lines and test output.</summary>
    public static string Describe(this AudioConfiguration configuration) => configuration switch
    {
        AudioConfiguration.WasapiOnly => "WASAPI only",
        AudioConfiguration.AsioOnly => "ASIO only",
        AudioConfiguration.Both => "WASAPI and ASIO together",
        _ => configuration.ToString(),
    };
}
