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

    /// <summary>This instance's own id, so several plugins in one DAW are told apart in the log.</summary>
    private readonly Guid instanceId = Guid.NewGuid();

    /// <summary>The plugin's log, gated by "Enable logs" in RemSound's Preferences exactly like the
    /// app's. It exists from construction so that even a failure during Initialize gets written down —
    /// a plugin that dies before it starts is precisely the report that arrives with no evidence.</summary>
    private PluginLog? log;

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
        var changed = sending != IsSending || !Equals(peer, chosenPeer);
        IsSending = sending;
        chosenPeer = peer;
        bridge?.ReceiveFrom(sending ? null : peer);
        // Only on a real change: this is called on every parameter touch, and a line per touch would
        // bury the one that mattered.
        if (changed) log?.Event(sending ? "job: sending this track to the peers" : $"job: receiving {peer?.ToString() ?? "nobody"} onto this track");
    }

    private System.Net.IPAddress? chosenPeer;

    /// <summary>Gate seams: drive the real job change and the real parameter write, and read back what
    /// was persisted, rather than a parallel copy of either.</summary>
    internal void SetJobForTest(bool sending, System.Net.IPAddress? peer) => SetJob(sending, peer);
    internal void PushJobToParametersForTest(bool sending, System.Net.IPAddress? peer) => PushJobToParameters(sending, peer);
    internal string? SavedPeerAddressForTest => lastPeerAddressText;

    /// <summary>Which peer this instance resolved to - for the gate, so "the parameter reached the
    /// engine" is checked rather than assumed.</summary>
    internal System.Net.IPAddress? ChosenPeerForTest => chosenPeer;

    private AudioPluginParameter? jobParameter;
    private AudioPluginParameter? peerParameter;
    private AudioPluginParameter? activeParameter;

    /// <summary>Take what the parameters now say and make it so. Shared by the parameter list and the
    /// window, deliberately: two routes to the same decision must not be two implementations of it,
    /// or the fallback would drift into behaving differently from the thing it is a fallback for.</summary>
    internal void ApplyParameters()
    {
        if (bridge is null) return;
        // EditValue, not ProcessValue: these are decisions a person makes, not automation curves the
        // host ramps per sample. ProcessValue only catches up on the audio thread, so reading it here
        // would apply the PREVIOUS choice - the setting would appear to lag one change behind.
        var active = activeParameter is null || activeParameter.EditValue >= 0.5;
        var receiving = jobParameter is not null && jobParameter.EditValue >= 0.5;

        System.Net.IPAddress? peer = null;
        if (active && receiving && peerParameter is not null)
        {
            var peers = bridge.KnownPeers;

            // The remembered ADDRESS wins. Somebody joining or leaving reshuffles the list, and a
            // restored project that went by position alone would quietly put a stranger on the track.
            if (lastPeerAddressText is not null && System.Net.IPAddress.TryParse(lastPeerAddressText, out var saved)
                && peers.Any(p => p.Address.Equals(saved)))
            {
                peer = saved;
            }
            else
            {
                // One-based, and out of range means NOBODY rather than wrapping round to somebody
                // else. Wrapping would put a stranger on the track when a peer disconnects, which is
                // far worse than silence.
                var index = (int)Math.Round(peerParameter.EditValue) - 1;
                if (index >= 0 && index < peers.Count) peer = peers[index].Address;
            }
        }

        SetJob(!active || !receiving, peer);
    }

    /// <summary>Keep the parameters in step when the WINDOW is what changed. Without this, opening the
    /// host's parameter list after using the window would show stale values and one nudge would undo
    /// the user's choice.</summary>
    private void PushJobToParameters(bool sending, System.Net.IPAddress? peer)
    {
        // BOTH values, not just EditValue. The host saves ProcessValue, so a window that wrote only
        // EditValue left every parameter sitting at its default — which is why the plugin reverted to
        // "send" within seconds of leaving the window, and why a saved project came back with job=0
        // whatever it had been doing. The saved state chunk recorded all three at their defaults, and
        // that was this bug written to disk.
        SetParameter(jobParameter, sending ? 0 : 1);
        if (peerParameter is null || bridge is null) return;
        var peers = bridge.KnownPeers;
        var index = peer is null ? 0 : peers.ToList().FindIndex(p => p.Address.Equals(peer)) + 1;
        SetParameter(peerParameter, Math.Max(0, index));
        if (peer is not null) lastPeerAddressText = peer.ToString();
    }

    private static void SetParameter(AudioPluginParameter? parameter, double value)
    {
        if (parameter is null) return;
        parameter.EditValue = value;
        parameter.ProcessValue = value;
    }

    /// <summary>The peer's ADDRESS as text, remembered alongside the index.
    ///
    /// <para>The parameter can only hold a number, and a position in a list is not a person: reopen a
    /// project after somebody joined or left and position 2 is somebody else. The address is written
    /// into the saved state as well, and on restore it wins — the index is only the fallback when that
    /// person is no longer connected.</para></summary>
    private string? lastPeerAddressText;

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

        // THREE INHERITED PROPERTIES, EACH FATAL IF LEFT ALONE. All three were missed in the first
        // build, and each failed silently with nothing the host could show a user (Anthony Reyers,
        // 2026-08-16, who proved every one of them by controlled experiment against the real bridge).
        //
        // Contact: AudioPlugSharpFactory marshals Company, Website and Contact into a VST3
        // PFactoryInfo. A null string becomes a null char*, the copy dereferences it, the factory
        // constructor throws, and GetPluginFactory returns NULL — so the host sees a DLL containing no
        // plugins at all. THIS is why RemSound never appeared in any effects list.
        Contact = "https://github.com/Ednunp/RemSound";

        // HasUserInterface: defaults to FALSE in the base constructor, and the VST3 controller refuses
        // to build a view when it is false. The whole accessible window existed and was never once
        // asked to appear; what a screen reader found instead was the host's own generic parameter
        // panel, which shows our parameter and port names and so looks convincingly like ours.
        HasUserInterface = true;

        // CacheLoadContext: defaults to false, so every instance built a fresh load context and
        // reloaded the entire RemSound stack — two plugins meant two independent copies of Core,
        // Sender and Receiver. That is also what desynchronises AudioPlugSharp's processor/controller
        // pairing, and a controller initialising against a plugin whose Initialize() has not run walks
        // a null Parameters list in unguarded native code and takes the DAW down with it.
        CacheLoadContext = true;

        // PORTS IN THE CONSTRUCTOR, not in Initialize(). The host calls SetMaxAudioBufferSize — which
        // is what allocates each port's buffers — on its own schedule, and it did so before Initialize
        // had created the ports. The ports were then replaced by fresh ones that nobody ever sized, so
        // every single audio block threw ArgumentNullException inside PreProcess and no input was ever
        // read. That is why send mode was silent in both directions.
        //
        // Stereo in, stereo out. IN is the track feeding your peers; OUT is what arrives from them.
        // Both ports exist in both jobs so the DAW's routing never changes when the user switches an
        // instance between sending and receiving — a track that rewires itself mid-session is a nasty
        // surprise, especially for someone navigating by screen reader.
        InputPorts = [monitorIn = new AudioIOPortManaged("Track in", EAudioChannelConfiguration.Stereo)];
        OutputPorts = [peerOut = new AudioIOPortManaged("Peer out", EAudioChannelConfiguration.Stereo)];

        log = new PluginLog(instanceId);
        log.SnapshotSource = DescribeForLog;
        log.Event($"plugin constructed (RemSound {PluginVersion})");
    }

    /// <summary>The once-a-second line. Everything needed to answer "why did it sound wrong" without
    /// having to ask another question: what this instance was doing, whether the app was answering,
    /// what rate and block size the host was running, whether we were resampling, and how full the
    /// buffer was. Read on the log's timer thread, never on the audio thread.</summary>
    private PluginLogSnapshot DescribeForLog()
    {
        var b = bridge;
        return new PluginLogSnapshot(
            Job: IsSending ? "send" : "receive",
            Peer: chosenPeer?.ToString(),
            Connected: b?.Connected ?? false,
            HostSampleRate: preparedSampleRate,
            BlockFrames: preparedBlockSize,
            Resampling: Math.Abs(preparedSampleRate - WireSampleRate) > 0.5,
            BlocksOut: b?.SentBlocks ?? 0,
            BlocksIn: b?.ServedBlocks ?? 0,
            ShortBlocks: b?.StarvedBlocks ?? 0,
            RingFrames: b?.RingFrames ?? 0,
            BytesOut: b?.BytesSent ?? 0,
            BytesIn: b?.BytesReceived ?? 0,
            KnownPeers: b?.KnownPeers.Count ?? 0);
    }

    /// <summary>The host telling us its largest block. Recorded as well as applied: if anything ever
    /// replaces a port after this has been called, the replacement would otherwise carry unallocated
    /// buffers and every block would throw. Cheap insurance against the exact fault that made send
    /// mode silent.</summary>
    public override void SetMaxAudioBufferSize(uint maxSamples, EAudioBitsPerSample bitsPerSample)
    {
        base.SetMaxAudioBufferSize(maxSamples, bitsPerSample);
        hostMaxSamples = maxSamples;
        hostBitsPerSample = bitsPerSample;
        log?.Event($"host buffer size: up to {maxSamples} samples, {bitsPerSample}");
    }

    private uint hostMaxSamples;
    private EAudioBitsPerSample hostBitsPerSample = EAudioBitsPerSample.Bits64;

    public override void Initialize()
    {
        base.Initialize();

        // Ports are built in the constructor, but re-apply whatever size the host has already asked
        // for, so they can never be left unallocated whichever order the host calls things in.
        if (hostMaxSamples > 0) base.SetMaxAudioBufferSize(hostMaxSamples, hostBitsPerSample);

        // The app owns the one connection; this is our end of the link to it. Opened here rather than
        // in the constructor because a DAW constructs plugins to inspect them without ever running
        // them, and a scan of the plugin folder should not open sockets.
        bridge = new PluginBridgeClient(id: instanceId);
        bridge.Notable += message => log?.Event($"link: {message}");
        capture = new HostCaptureBackend(samples => bridge?.SendTrackBlock(samples.Span));
        render = new PeerRenderBridge(bridge);
        bridge.Hello();
        log?.Event($"initialised - host says {Host?.SampleRate ?? 0:0} Hz, up to {Host?.MaxAudioBufferSize ?? 0} frames per block; "
                 + $"said hello to RemSound on 127.0.0.1:{PluginBridgeProtocol.DefaultPort}");

        // THE SCREEN-READER FALLBACK. The window is the intended way to work this plugin, and it is
        // ordinary WinForms precisely so NVDA can read it. But keyboard focus across a host's plugin
        // frame is the one thing that cannot be proven outside a real DAW, and if a host gets it
        // wrong the window becomes unreachable with no way back.
        //
        // Named parameters are that way back: every DAW exposes a plain parameter list, and in Reaper
        // with OSARA that list is fully keyboard-navigable and spoken. So the same three decisions the
        // window offers are also parameters, with names that make sense read aloud out of context —
        // "Receive instead of send", not "Mode".
        //
        // They are also what a host would automate, which is harmless here: nobody automates who is on
        // a track mid-take, but a host that saves parameter values gets the instance's setup restored
        // with the session for free.
        AddParameter(jobParameter = new AudioPluginParameter
        {
            ID = "job",
            Name = "Receive instead of send",
            ValueFormat = "0",
            MinValue = 0,
            MaxValue = 1,
            DefaultValue = 0,
        });
        AddParameter(peerParameter = new AudioPluginParameter
        {
            ID = "peer",
            // One-based when spoken: "peer 1" is the first person in RemSound's list, which is what
            // somebody counting down a list expects. A zero would be read as "none" by anyone sane.
            Name = "Which peer to receive",
            ValueFormat = "0",
            MinValue = 0,
            MaxValue = 32,
            DefaultValue = 0,
        });
        AddParameter(activeParameter = new AudioPluginParameter
        {
            ID = "active",
            Name = "Active",
            ValueFormat = "0",
            MinValue = 0,
            MaxValue = 1,
            DefaultValue = 1,
        });

        // Applied off the audio thread. A parameter change sends a datagram and touches the claim
        // register; doing that from Process() would put a syscall on the DAW's audio thread for the
        // sake of a decision the user makes once.
        jobParameter.PropertyChanged += (_, _) => ApplyParameters();
        peerParameter.PropertyChanged += (_, _) => ApplyParameters();
        activeParameter.PropertyChanged += (_, _) => ApplyParameters();
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
            var first = preparedBlockSize == 0;
            var previousBlock = preparedBlockSize;
            var previousRate = preparedSampleRate;
            preparedBlockSize = blockSize;
            preparedSampleRate = rate;
            capture?.PrepareForBlockSize(blockSize, rate);
            render?.PrepareForBlockSize(blockSize, rate);
            // Buffers are resized OFF the audio thread's steady path, and only on a real change, so
            // this line is rare. If it ever appears repeatedly in a log, that is the finding: the host
            // is changing its block size every callback and we are allocating in its audio thread.
            log?.Event(first
                ? $"audio started: {rate:0} Hz, {blockSize} frames per block"
                  + (Math.Abs(rate - WireSampleRate) > 0.5 ? $" - resampling to and from {WireSampleRate} Hz" : " - no resampling needed")
                : $"host changed format: {previousRate:0} Hz/{previousBlock} frames -> {rate:0} Hz/{blockSize} frames (one block of silence while buffers resize)");
            outLeft.Clear();
            outRight.Clear();
            return;
        }

        if (IsSending)
        {
            // Sending: this track goes to the peers through the app's connection, AND passes straight
            // through to the output.
            //
            // The first build cleared the output instead, on the reasoning that the track was already
            // audible and a second copy would double against itself. That reasoning was simply wrong,
            // and Anthony Reyers put it plainly: effects are in SERIES. A plugin that outputs silence
            // silences the track from that point on, and there is no other copy of the signal for it
            // to double against. It made the plugin unusable on any normal FX chain.
            var inLeft = monitorIn.GetAudioBuffer(0);
            var inRight = monitorIn.GetAudioBuffer(1);
            capture?.SubmitHostBlock(inLeft, inRight);
            var passthrough = Math.Min(blockSize, Math.Min(inLeft.Length, inRight.Length));
            for (var i = 0; i < passthrough; i++) { outLeft[i] = inLeft[i]; outRight[i] = inRight[i]; }
            for (var i = passthrough; i < blockSize; i++) { outLeft[i] = 0; outRight[i] = 0; }
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
        {
            var peer = peerText is not null && System.Net.IPAddress.TryParse(peerText, out var a) ? a : null;
            SetJob(sending, peer);
            PushJobToParameters(sending, peer);
        };

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
        // Whether the host gave us a window to parent into is worth knowing: a zero handle is the
        // shape of "the plugin window never appeared", and it is otherwise invisible from a bug report.
        log?.Event($"window opened ({EditorWidth}x{EditorHeight}, host frame {(parentWindow == IntPtr.Zero ? "NOT supplied" : "supplied")})");
    }

    public override void HideEditor()
    {
        log?.Event("window closed");
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

        // DELIBERATELY STABLE TEXT. The first version put live block counts in here, so the status
        // changed every single second — and every change rewrote the control, which threw keyboard
        // focus back to the first field and made the window nearly unusable with a screen reader
        // (Anthony Reyers, 2026-08-16). A status line that never settles is not a status line.
        //
        // Trouble is still reported, but as a STATE rather than a running total: it appears when
        // blocks start arriving short and goes away when they stop, so the text changes twice rather
        // than sixty times a minute.
        var served = bridge.ServedBlocks;
        var starved = bridge.StarvedBlocks;
        var struggling = served > 200 && starved > served / 20;   // more than one block in twenty
        return $"Receiving {chosenPeer} onto this track." + Environment.NewLine
             + (struggling ? "Audio is arriving short - see the plugin log." : "Running normally.");
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
        log?.Event($"deactivated by the host - peer released. Totals: {bridge?.ServedBlocks ?? 0} blocks in, "
                 + $"{bridge?.SentBlocks ?? 0} out, {bridge?.StarvedBlocks ?? 0} short");
    }

    /// <summary>Save this instance with the host, and add the peer's ADDRESS to what the base class
    /// writes.
    ///
    /// <para>A parameter can only hold a number, and a position in a list is not a person: reopen a
    /// project after somebody joined or left and position two is somebody else. The address is
    /// appended after the base class's own state, and on restore it wins — the position is only the
    /// fallback for when that person is no longer connected.</para></summary>
    public override byte[] SaveState()
    {
        var baseState = base.SaveState() ?? [];
        var address = System.Text.Encoding.UTF8.GetBytes(lastPeerAddressText ?? "");
        var combined = new byte[baseState.Length + address.Length + Marker.Length + sizeof(int)];
        Buffer.BlockCopy(baseState, 0, combined, 0, baseState.Length);
        Buffer.BlockCopy(Marker, 0, combined, baseState.Length, Marker.Length);
        BitConverter.GetBytes(address.Length).CopyTo(combined, baseState.Length + Marker.Length);
        Buffer.BlockCopy(address, 0, combined, baseState.Length + Marker.Length + sizeof(int), address.Length);
        return combined;
    }

    public override void RestoreState(byte[] stateData)
    {
        var baseLength = stateData?.Length ?? 0;
        if (stateData is not null)
        {
            // Find OUR marker at the tail. Anything without one is a chunk from an older build, which
            // must still load — refusing it would lose the user's whole plugin instance.
            var at = LastIndexOf(stateData, Marker);
            if (at >= 0 && at + Marker.Length + sizeof(int) <= stateData.Length)
            {
                var length = BitConverter.ToInt32(stateData, at + Marker.Length);
                var from = at + Marker.Length + sizeof(int);
                if (length >= 0 && from + length <= stateData.Length)
                {
                    var text = System.Text.Encoding.UTF8.GetString(stateData, from, length);
                    lastPeerAddressText = string.IsNullOrEmpty(text) ? null : text;
                    baseLength = at;
                }
            }
        }

        if (stateData is not null && baseLength != stateData.Length)
        {
            var trimmed = new byte[baseLength];
            Buffer.BlockCopy(stateData, 0, trimmed, 0, baseLength);
            base.RestoreState(trimmed);
        }
        else base.RestoreState(stateData!);

        ApplyParameters();
        log?.Event($"state restored - job {(IsSending ? "send" : "receive")}, peer {lastPeerAddressText ?? "(by position)"}");
    }

    private static readonly byte[] Marker = System.Text.Encoding.ASCII.GetBytes("<!--RemSoundPeer:");

    private static int LastIndexOf(byte[] haystack, byte[] needle)
    {
        for (var i = haystack.Length - needle.Length; i >= 0; i--)
        {
            var hit = true;
            for (var j = 0; j < needle.Length; j++) if (haystack[i + j] != needle[j]) { hit = false; break; }
            if (hit) return i;
        }
        return -1;
    }

    /// <summary>Tear the instance down completely. AudioPlugSharp gives a plugin no dispose hook — a
    /// host just unloads the process — so this exists for the gate, which creates real instances and
    /// must not leave their sockets and log timers running behind it.</summary>
    internal void CloseForTest()
    {
        bridge?.Dispose();
        bridge = null;
        capture?.Dispose();
        capture = null;
        log?.Event("closed");
        log?.Dispose();
        log = null;
    }

    public override void Start()
    {
        base.Start();
        capture?.Start([]);
        log?.Event("activated by the host");
        // Ask again who is available: RemSound may have been started, or its peers changed, while
        // this instance sat inactive in a saved session.
        bridge?.Hello();
        if (!IsSending) bridge?.ReceiveFrom(chosenPeer);
    }
}
