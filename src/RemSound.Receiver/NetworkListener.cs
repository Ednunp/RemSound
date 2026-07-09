using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using RemSound.Core;

namespace RemSound.Receiver;

/// <summary>
/// Owns the UDP receive socket and a single dedicated foreground thread that drains it.
/// Hands raw packets (byte buffer + length + remote endpoint) up to a callback supplied by the
/// owner — has no idea what's inside the packets.
///
/// The receive buffer is a single fixed array reused across calls. NOTE: the current
/// <see cref="Socket.ReceiveFrom(byte[], int, int, SocketFlags, ref EndPoint)"/> overload still
/// allocates a SocketAddress + a fresh IPEndPoint per datagram — tiny (tens of bytes) and dwarfed
/// by decode/mix work, but not literally allocation-free. A future optimisation could switch to the
/// Span/SocketAddress overload with a cached SocketAddress and only materialise an IPEndPoint when a
/// new session actually opens.
/// </summary>
internal sealed class NetworkListener : IDisposable
{
    private readonly Action<byte[], int, IPEndPoint> onPacket;
    private readonly Action<string> onDiagnostic;
    private CancellationTokenSource? cts;
    private Socket? socket;
    private Thread? thread;

    // Time-in-user-handler instrumentation. We measure from "ReceiveFrom returned" to
    // "onPacket returned" so the SNAP can split observed inter-packet jitter at the
    // receiver. If this metric is consistently in the multi-ms range, the receiver's
    // own processing chain is the source of the gap (lock contention with the audio
    // thread, GC, decode work backing up) rather than the network or the sender.
    private long maxOnPacketTicks;
    public int TakeMaxOnPacketMs() =>
        (int)(Interlocked.Exchange(ref maxOnPacketTicks, 0) * 1000 / Stopwatch.Frequency);

    // Inter-packet arrival gap at the user-space socket. Measures the elapsed time between
    // consecutive `ReceiveFrom` returns. This is the diagnostic that splits "the sender
    // stalled" from "the network jittered" from "the OS sat on packets before delivering
    // them to our process" — by comparing this with the sender's per-callback gap on the
    // other machine, we can tell which side introduced the arrival gap that triggered a
    // concealment-fire / underrun. The probe is gated on DiagnosticsGate.Enabled exactly
    // like the OnPacket timer above; pays nothing when logs are off. 2026-05-21.
    private long maxInterPacketGapTicks;
    private long lastReceiveTicks;
    public int TakeMaxInterPacketGapMs() =>
        (int)(Interlocked.Exchange(ref maxInterPacketGapTicks, 0) * 1000 / Stopwatch.Frequency);

    // CUMULATIVE on-packet work-time counter. Sister to maxOnPacketTicks (per-call max)
    // — this is "total time the receive thread spent inside the packet handler since the
    // last Take". The diag log samples this once a second and reports milliseconds-of-
    // CPU-per-second for the receive thread, which is the per-thread CPU% reading from
    // item 2 of RemSoundefficiency.md. Cumulative-sum + atomic-take pattern; no lock.
    // 2026-05-22.
    private long cumulativeOnPacketTicks;
    public long TakeCumulativeOnPacketTicks() => Interlocked.Exchange(ref cumulativeOnPacketTicks, 0);

    public NetworkListener(Action<byte[], int, IPEndPoint> onPacket, Action<string> onDiagnostic)
    {
        this.onPacket = onPacket;
        this.onDiagnostic = onDiagnostic;
    }

    public bool IsRunning => socket is not null;

    public void Start(int udpPort)
    {
        Stop();
        cts = new CancellationTokenSource();
        socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        // 1 MB kernel receive buffer — large enough to ride out ~30 ms of audio-thread or GC
        // stall at typical PCM-stereo bitrates before the kernel starts dropping inbound
        // datagrams. Cheap on modern Windows; the old 512 KB cap was below what the receive
        // thread can saturate during a long-tail GC pause.
        socket.ReceiveBufferSize = 1024 * 1024;
        socket.Bind(new IPEndPoint(IPAddress.Any, udpPort));

        var startedSocket = socket;
        var token = cts.Token;
        thread = new Thread(() => ReceiveLoop(startedSocket, token))
        {
            IsBackground = true,
            Name = "RemSound.Receive",
        };
        thread.Start();
        onDiagnostic($"network listener bound to UDP :{udpPort}");
    }

    public void Stop()
    {
        cts?.Cancel();
        try { socket?.Close(); } catch { /* ignore */ }
        socket = null;
        try { thread?.Join(500); } catch { /* ignore */ }
        thread = null;
        cts?.Dispose();
        cts = null;
        // Reset the inter-packet timestamp so a Restart doesn't measure the long pause
        // between the previous session's last packet and the new session's first as a
        // spurious huge gap.
        Interlocked.Exchange(ref lastReceiveTicks, 0);
        Interlocked.Exchange(ref maxInterPacketGapTicks, 0);
        Interlocked.Exchange(ref cumulativeOnPacketTicks, 0);
    }

    public void Dispose() => Stop();

    private void ReceiveLoop(Socket activeSocket, CancellationToken token)
    {
        using var threadBoost = new WindowsAudioThreadBoost("Capture");
        var buffer = new byte[2048];
        EndPoint anyEndpoint = new IPEndPoint(IPAddress.Any, 0);

        while (!token.IsCancellationRequested)
        {
            int received;
            try
            {
                received = activeSocket.ReceiveFrom(buffer, 0, buffer.Length, SocketFlags.None, ref anyEndpoint);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.Interrupted) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; }
            catch (OperationCanceledException) { break; }

            if (received <= 0) continue;
            if (anyEndpoint is not IPEndPoint remote) continue;

            try
            {
                // Dispatch timing feeds the SNAP's rxDispMs column. Skipped when diagnostics
                // are off so the receive loop isn't paying two Stopwatch reads + a CAS loop
                // per packet for a number nobody is going to log.
                if (RemSound.Core.DiagnosticsGate.Enabled)
                {
                    var nowTicks = Stopwatch.GetTimestamp();
                    // Inter-packet arrival gap. First packet seeds lastReceiveTicks without
                    // recording a gap (no previous to compare to). Subsequent packets compute
                    // elapsed-since-previous-ReceiveFrom-returned. The window includes our
                    // onPacket processing, but that's typically sub-millisecond — so a spike
                    // here points at the OS/network layer below us, not at our dispatch work.
                    // Our work shows up separately in maxOnPacketTicks.
                    var prevReceiveTicks = Interlocked.Exchange(ref lastReceiveTicks, nowTicks);
                    if (prevReceiveTicks != 0)
                    {
                        var gap = nowTicks - prevReceiveTicks;
                        long curGap;
                        do { curGap = Volatile.Read(ref maxInterPacketGapTicks); }
                        while (gap > curGap && Interlocked.CompareExchange(ref maxInterPacketGapTicks, gap, curGap) != curGap);
                    }
                    var dispatchStart = nowTicks;
                    onPacket(buffer, received, remote);
                    var elapsed = Stopwatch.GetTimestamp() - dispatchStart;
                    long current;
                    do { current = Volatile.Read(ref maxOnPacketTicks); }
                    while (elapsed > current && Interlocked.CompareExchange(ref maxOnPacketTicks, elapsed, current) != current);
                    // And the cumulative counter — every call's elapsed adds in. Lets the
                    // diag log show "the receive thread spent X ms working this second"
                    // (item 2 of the efficiency analysis).
                    Interlocked.Add(ref cumulativeOnPacketTicks, elapsed);
                }
                else
                {
                    onPacket(buffer, received, remote);
                }
            }
            catch (Exception ex)
            {
                onDiagnostic($"packet handler threw: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
