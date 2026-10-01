using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// Jamulus-style Opus frames, automatic (Ed, 2026-10-01; until then the last switch on Preferences' Latency lab tab). The
/// window's part: load the library at start-up, here on the UI thread and never first on an audio thread; and once a
/// second say whether this computer plays through ASIO, which is what it tells the others in its Format packets and
/// heartbeats. The rule itself is in <see cref="JamulusStyle"/>.
/// </summary>
public sealed partial class MainForm
{
    /// <summary>Start-up: load the custom-modes Opus library and say how it went. If it will not load, this copy neither
    /// sends nor asks for the frames, and every stream stays the normal one.</summary>
    private void WireJamulusStyle()
    {
        OpusCustomNative.TryLoad();
        logFile.Event($"Jamulus-style Opus: library {OpusCustomNative.LoadMessage}");
        KeepJamulusStyleInStep();
    }

    /// <summary>Once a second (and at start-up): this computer plays through ASIO when it is receiving with an ASIO output
    /// ticked, alone or beside WASAPI ones. Logged when it changes, with the configuration.</summary>
    private void KeepJamulusStyleInStep()
    {
        var plays = (ReceiverRunningForTest?.Invoke() ?? receiver.IsRunning) && ActiveAudioConfiguration().UsesAsio();
        if (plays == JamulusStyle.PlaysThroughAsio && jamulusStyleLogged) return;
        JamulusStyle.PlaysThroughAsio = plays;
        jamulusStyleLogged = true;
        logFile.Event($"Jamulus-style Opus: {JamulusStyle.Describe()} [{ActiveAudioConfiguration().Describe()}]");
    }

    private bool jamulusStyleLogged;
}
