using System.Net;
using System.Net.Sockets;

namespace RemSound.Core;

/// <summary>
/// One end of the app↔plugin link. The app opens one of these as the HOST; each plugin instance
/// opens one as a CLIENT.
///
/// <para><b>Loopback only, enforced not assumed.</b> The socket binds 127.0.0.1 and refuses to send
/// anywhere else. A bug that let this reach a routable address would stream a peer's decrypted audio
/// onto the network in the clear, so the restriction is checked on every send rather than trusted to
/// configuration.</para>
///
/// <para><b>UDP on purpose.</b> It is the lowest-latency option available without shared memory, and
/// on loopback the OS does not drop or reorder in practice. Losing an occasional audio block would
/// cost one buffer of silence, which is the same failure the network path already handles — whereas
/// TCP's retransmit-and-wait would convert a lost block into a stall, which is worse for audio.</para>
///
/// <para><b>Receiving happens on its own thread</b> and hands blocks over by callback. The DAW's
/// audio thread never blocks on a socket: it drops its outgoing block into the socket and returns.</para>
/// </summary>
public sealed class PluginBridgeLink : IDisposable
{
    private static readonly IPAddress Loopback = IPAddress.Loopback;

    private readonly Socket socket;
    private readonly Thread receiveThread;
    private readonly byte[] receiveBuffer = new byte[PluginBridgeProtocol.HeaderSize + PluginBridgeProtocol.MaxAudioBytes];
    private volatile bool running = true;

    /// <summary>The port this end is bound to. For the host that is the well-known port; for a client
    /// it is whatever the OS assigned, which is how the host knows where to send replies.</summary>
    public int Port { get; }

    /// <summary>Raised for every valid message, on the receive thread. Handlers must be quick and
    /// must not throw — a throw here would take down the link (and, in a plugin, annoy a DAW).</summary>
    public event Action<PluginBridgeMessage, int, IPAddress?, ReadOnlyMemory<byte>, IPEndPoint>? MessageReceived;

    /// <summary>Messages that arrived but were not valid bridge messages. Surfaced rather than
    /// silently swallowed: a rising count means something else is talking to this port.</summary>
    public long MalformedReceived { get; private set; }

    public PluginBridgeLink(int port)
    {
        socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        // Bind to loopback explicitly. Binding to Any would expose the link to the network.
        socket.Bind(new IPEndPoint(Loopback, port));
        Port = ((IPEndPoint)socket.LocalEndPoint!).Port;
        receiveThread = new Thread(ReceiveLoop)
        {
            IsBackground = true,
            Name = "remsound-plugin-bridge",
            // Audio-adjacent: this thread carries blocks that a DAW is waiting on.
            Priority = ThreadPriority.AboveNormal,
        };
        receiveThread.Start();
    }

    /// <summary>Send one message. <paramref name="payload"/> may be empty for control messages.
    /// Never throws on a routine socket failure — the far end going away is normal (a DAW closed),
    /// and an exception on the audio thread would be far worse than a dropped block.</summary>
    public bool Send(IPEndPoint destination, PluginBridgeMessage type, int instanceHash, IPAddress? peer, ReadOnlySpan<byte> payload)
    {
        // The restriction that matters: this link never leaves the machine.
        if (!destination.Address.Equals(Loopback)) return false;
        if (payload.Length > PluginBridgeProtocol.MaxAudioBytes) return false;

        // Sized to what is actually being sent, NOT to the maximum.
        //
        // This used to reserve HeaderSize + MaxAudioBytes — 32,784 bytes — on every call. C# zeroes a
        // stackalloc, so each call memset 32 KB regardless of payload, and this runs on the DAW's
        // audio thread once per block: a receiving instance was clearing 32 KB to send a 20-byte
        // request. At a 128-frame block that is around 12 MB/s of pointless writes on the one thread
        // in the process that must never be made to work for nothing, and it is exactly the shape
        // that produces a "RemSound makes my DAW crackle" report with nothing in the log to show for
        // it. Found 2026-08-24 reading the file against its own promise two classes over: "nothing
        // allocates on the audio thread".
        var total = PluginBridgeProtocol.HeaderSize + payload.Length;
        Span<byte> packet = stackalloc byte[total];
        PluginBridgeProtocol.WriteHeader(packet, type, instanceHash, peer, payload.Length);
        payload.CopyTo(packet[PluginBridgeProtocol.HeaderSize..]);
        try
        {
            socket.SendTo(packet, SocketFlags.None, destination);
            return true;
        }
        catch (SocketException) { return false; }
        catch (ObjectDisposedException) { return false; }
    }

    private void ReceiveLoop()
    {
        EndPoint from = new IPEndPoint(Loopback, 0);
        while (running)
        {
            int received;
            try { received = socket.ReceiveFrom(receiveBuffer, ref from); }
            catch (SocketException) { if (!running) return; continue; }
            catch (ObjectDisposedException) { return; }

            var packet = receiveBuffer.AsSpan(0, received);
            if (!PluginBridgeProtocol.TryReadHeader(packet, out var type, out var hash, out var peer, out var length))
            {
                MalformedReceived++;
                continue;
            }
            // Ignore anything that didn't come from this machine, belt and braces alongside the bind.
            if (from is not IPEndPoint sender || !sender.Address.Equals(Loopback)) { MalformedReceived++; continue; }

            try
            {
                MessageReceived?.Invoke(type, hash, peer,
                    new ReadOnlyMemory<byte>(receiveBuffer, PluginBridgeProtocol.HeaderSize, length), sender);
            }
            catch
            {
                // A handler that throws must not kill the link — in a plugin that would look to the
                // user like RemSound taking their DAW down.
            }
        }
    }

    public void Dispose()
    {
        running = false;
        try { socket.Close(); } catch { }
        try { socket.Dispose(); } catch { }
        try { receiveThread.Join(500); } catch { }
    }
}
