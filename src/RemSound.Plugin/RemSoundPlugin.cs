using System.Drawing;
using System.Windows.Forms;
using AudioPlugSharp;
using RemSound.App;
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
    public const int WireSampleRate = PluginBridgeProtocol.WireSampleRate;

    private AudioIOPort<double>? monitorIn;
    private AudioIOPort<double>? peerOut;

    private PluginBridgeClient? bridge;
    private HostCaptureBackend? capture;
    private PeerRenderBridge? render;
    private int preparedBlockSize;
    private double preparedSampleRate;

    /// <summary>What this instance is doing. One person per track: it either sends this track to the
    /// peers, or receives ONE peer onto it — never both, which is what keeps the DAW routing stable
    /// and the double-audio guard simple.</summary>
    internal bool IsSending { get; private set; } = true;

    /// <summary>The link to the app, for the editor panel to read peers and status from.</summary>
    internal PluginBridgeClient? Bridge => bridge;

    /// <summary>Switch this instance between sending and receiving one peer. Releasing the old peer
    /// happens inside the client, so the app gets it back through the speakers immediately rather
    /// than after a timeout.</summary>
    internal void SetJob(bool sending, System.Net.IPAddress? peer)
    {
        IsSending = sending;
        chosenPeer = peer;
        bridge?.ReceiveFrom(sending ? null : peer);
    }

    private System.Net.IPAddress? chosenPeer;

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

        // The app owns the one connection; this is our end of the link to it. Opened here rather than
        // in the constructor because a DAW constructs plugins to inspect them without ever running
        // them, and a scan of the plugin folder should not open sockets.
        bridge = new PluginBridgeClient();
        capture = new HostCaptureBackend(samples => bridge?.SendTrackBlock(samples.Span));
        render = new PeerRenderBridge(bridge);
        bridge.Hello();

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
        // they are taken one at a time rather than gathered into an array — a restriction that is
        // welcome here, because it keeps this method allocation-free.
        if (peerOut is null || monitorIn is null) return;
        var outLeft = peerOut.GetAudioBuffer(0);
        var outRight = peerOut.GetAudioBuffer(1);

        // The host can change its block size or rate between blocks. Growing buffers here would
        // allocate on the audio thread — the one thing that must never happen, because .NET stops
        // every thread in the process to collect, which inside a DAW means every plugin in the
        // session. So we resize only when it actually changed, and output silence for that one block.
        var blockSize = outLeft.Length;
        var rate = Host?.SampleRate ?? WireSampleRate;
        if (blockSize != preparedBlockSize || Math.Abs(rate - preparedSampleRate) > 0.5)
        {
            preparedBlockSize = blockSize;
            preparedSampleRate = rate;
            capture?.PrepareForBlockSize(blockSize, rate);
            render?.PrepareForBlockSize(blockSize, rate);
            outLeft.Clear();
            outRight.Clear();
            return;
        }

        if (IsSending)
        {
            // Sending: this track goes to the peers through the app's connection. The output stays
            // SILENT rather than echoing the input — the track is already audible in the DAW, and a
            // second copy of it out of our port would double it against itself.
            capture?.SubmitHostBlock(monitorIn.GetAudioBuffer(0), monitorIn.GetAudioBuffer(1));
            outLeft.Clear();
            outRight.Clear();
            return;
        }

        // Receiving: the claimed peer lands on this track — and, because the app knows the claim, is
        // no longer coming out of the speakers as well. Anything the buffer could not supply is left
        // silent rather than repeated, so a short block sounds like a gap and not like a stutter.
        render?.FillHostBlock(outLeft, outRight);
    }

    // ---- The window. An ordinary WinForms panel parented into the DAW's plugin frame, which is the
    // whole accessibility bet: real WinForms controls expose themselves to NVDA for free, while most
    // commercial plugin windows are custom-drawn and unreadable. ----------------------------------

    private Form? editorForm;
    private PluginEditorPanel? editorPanel;

    public override void InitializeEditor()
    {
        base.InitializeEditor();
        EditorWidth = 420;
        EditorHeight = 300;
    }

    public override void ShowEditor(IntPtr parentWindow)
    {
        base.ShowEditor(parentWindow);
        editorPanel = new PluginEditorPanel { Dock = DockStyle.Fill };

        // Everything the panel shows comes from the app over the link — the peer list it offers and
        // the status it reports are RemSound's, not a second copy of them kept here that could
        // disagree. Ed's call: "read the profile from the standalone app... it's far easier."
        editorPanel.PeerSource = () => bridge?.KnownPeers.Select(p => (p.Address.ToString(), p.Name)).ToList() ?? [];
        editorPanel.StatusSource = DescribeStatus;
        editorPanel.JobChanged += (sending, peerText) =>
            SetJob(sending, peerText is not null && System.Net.IPAddress.TryParse(peerText, out var a) ? a : null);

        editorForm = new Form
        {
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            TopLevel = false,
            Width = (int)EditorWidth,
            Height = (int)EditorHeight,
        };
        editorForm.Controls.Add(editorPanel);

        // Parent into the host's frame. A borderless child form rather than raw child controls, so
        // WinForms keeps its own message loop and focus handling intact across the boundary — which
        // is what a screen reader relies on to see the controls at all.
        if (parentWindow != IntPtr.Zero) SetParent(editorForm.Handle, parentWindow);
        editorForm.Show();
    }

    public override void HideEditor()
    {
        editorForm?.Close();
        editorForm?.Dispose();
        editorForm = null;
        editorPanel = null;
        base.HideEditor();
    }

    public override void ResizeEditor(uint newWidth, uint newHeight)
    {
        base.ResizeEditor(newWidth, newHeight);
        if (editorForm is not null) editorForm.Size = new Size((int)newWidth, (int)newHeight);
    }

    /// <summary>The status line, in plain English. Says what is actually true rather than "OK" —
    /// including the awkward cases, because "the plugin isn't working" is the report we would
    /// otherwise get with nothing to go on.</summary>
    internal string DescribeStatus()
    {
        if (bridge is null) return "Starting up.";
        if (!bridge.Connected)
            return "RemSound is not answering." + Environment.NewLine
                 + "Start RemSound, or switch the link on in its DAW plugin menu.";
        if (IsSending)
            return "Sending this track to your peers." + Environment.NewLine
                 + $"{bridge.KnownPeers.Count} peer(s) connected in RemSound.";
        if (chosenPeer is null)
            return "Connected to RemSound." + Environment.NewLine + "Choose a peer to receive.";

        var served = bridge.ServedBlocks;
        var starved = bridge.StarvedBlocks;
        // Starved blocks are reported, not hidden. A handful at the start is the buffer filling; a
        // number that keeps climbing is the one fact that explains a crackle, and burying it would
        // send someone hunting through their network for a week.
        return $"Receiving {chosenPeer} onto this track." + Environment.NewLine
             + (starved == 0
                 ? $"{served} blocks, none missed."
                 : $"{served} blocks, {starved} arrived short.");
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetParent(IntPtr child, IntPtr newParent);

    /// <summary>The host has deactivated this instance — bypassed, removed, or the session closing.
    ///
    /// <para>Let the claimed peer go AT ONCE rather than waiting for the claim to lapse. An inactive
    /// plugin produces no audio, so a peer still claimed by one would be silent everywhere for five
    /// seconds with nothing on screen to explain it. Re-activating re-claims on the next block, since
    /// asking for audio IS the claim.</para>
    ///
    /// <para>The socket deliberately stays open: hosts stop and start instances routinely (transport
    /// changes, bypass), and tearing the link down each time would mean a fresh handshake and an empty
    /// buffer every time somebody hit bypass. If the DAW dies outright the claim lapses on its own —
    /// that timeout exists precisely for the case where nobody gets to say goodbye.</para></summary>
    public override void Stop()
    {
        base.Stop();
        capture?.Stop();
        bridge?.ReceiveFrom(null);
    }

    public override void Start()
    {
        base.Start();
        capture?.Start([]);
        // Ask again who is available: RemSound may have been started, or its peers changed, while
        // this instance sat inactive in a saved session.
        bridge?.Hello();
        if (!IsSending) bridge?.ReceiveFrom(chosenPeer);
    }
}
