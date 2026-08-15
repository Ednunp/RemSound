using AudioPlugSharp;
using RemSound.Core;

namespace RemSound.Plugin;

/// <summary>
/// RemSound as a VST3 plugin (v6.0).
///
/// <para>The same engine as the app — same protocol, same encryption, same jitter buffer, same relay
/// — with the sound card swapped for the DAW. Audio on the track goes to your peers; audio from a
/// peer arrives on the track, where it can be recorded, shaped and mixed like anything else.</para>
///
/// <para><b>One person per track</b> (Ed, 2026-08-15). An instance either SENDS this track, or
/// RECEIVES one chosen peer onto it. Splitting a single machine's devices into separate streams was
/// considered and deliberately dropped: each stream gets its own jitter buffer and its own drift
/// correction, so a guitar and a vocal from the same performance would slowly drift apart at the far
/// end — worse than latency, and it needs shared-clock work in the most delicate code we have.</para>
///
/// <para><b>Configuration comes from the app</b>, not from here. Password, peers and audio settings
/// are whatever you already set up in RemSound ("read the profile from the standalone app... it's
/// far easier"), so the plugin only has to answer what THIS instance is doing. That also means far
/// less UI to make accessible.</para>
///
/// <para><b>Known limit, stated plainly:</b> AudioPlugSharp's host interface exposes no way to report
/// plugin latency, so the DAW cannot delay-compensate for the network jitter buffer. SonoBus has the
/// same gap. Tracks recorded through the plugin will sit late by roughly the reported round trip and
/// need nudging. This is a framework limitation, not something we can fix from here — it is written
/// down so nobody rediscovers it as a bug.</para>
/// </summary>
public class RemSoundPlugin : AudioPluginBase
{
    /// <summary>What the wire speaks. The host may run at 44.1 or 96 kHz, so the boundary resamples —
    /// SonoBus has an open bug (their issue #1) where a mismatch audibly transposes the audio, and
    /// that is precisely the trap this constant exists to make visible.</summary>
    public const int WireSampleRate = 48000;

    private AudioIOPort<double>? monitorIn;
    private AudioIOPort<double>? peerOut;

    public RemSoundPlugin()
    {
        Company = "RemSound";
        Website = "https://github.com/Ednunp/RemSound";
        PluginName = "RemSound";
        PluginCategory = "Fx|Network";
        PluginVersion = "6.0.0";
        // Stable identity: a DAW keys saved sessions off this, so it must never change once shipped
        // or every existing project silently loses its RemSound instances.
        PluginID = 0x52656D536E640600; // "RemSnd" + 06 00
    }

    public override void Initialize()
    {
        base.Initialize();

        // Stereo in, stereo out. IN is the track feeding your peers; OUT is what arrives from them.
        // Both ports exist in both jobs so the DAW's routing never changes when the user switches an
        // instance between sending and receiving — a track that rewires itself mid-session is a
        // nasty surprise, especially for someone navigating by screen reader.
        InputPorts = [monitorIn = new AudioIOPortManaged("Track in", EAudioChannelConfiguration.Stereo)];
        OutputPorts = [peerOut = new AudioIOPortManaged("Peer out", EAudioChannelConfiguration.Stereo)];
    }

    public override void Process()
    {
        base.Process();

        // The DAW's block for this track, and the buffer we owe it back. Spans are stack-only, so
        // they are taken one at a time rather than gathered into an array — and that restriction is
        // welcome here: it keeps this method allocation-free, which is the first rule of the audio
        // thread (see the seam note below).
        if (peerOut is null) return;
        var outLeft = peerOut.GetAudioBuffer(0);
        var outRight = peerOut.GetAudioBuffer(1);

        // ENGINE SEAM — deliberately not wired yet. Two rules govern what goes here, and both matter
        // more than getting it working quickly:
        //   1. NOTHING may allocate on this thread. .NET has no real-time GC, so an allocation here
        //      can stall every plugin in the DAW, not just ours. Buffers are allocated at Initialize
        //      and reused; networking and encoding belong on another thread behind a ring buffer.
        //   2. The host's sample rate (Host.SampleRate) is NOT necessarily WireSampleRate, so the
        //      boundary must resample both ways or the audio arrives transposed.
        // Until it is wired, output silence rather than passing the track through: a plugin that
        // silently echoes its input would be indistinguishable from one that is working.
        outLeft.Clear();
        outRight.Clear();
    }
}
