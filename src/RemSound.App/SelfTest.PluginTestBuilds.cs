using System.Buffers.Binary;
using System.Net;
using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// The plugin link without its test builds. Ed, 2026-09-13: there was no plugin before 6.0, and 6.0 is the first
/// release, so the link stopped speaking the messages only the test builds used.
/// </summary>
internal static partial class SelfTest
{
    /// <summary>
    /// THE TEST BUILDS' MESSAGE TYPES ARE REFUSED.
    ///
    /// <para>The one-peer claim (type 2) and its reply (type 5) went with the test builds. Both numbers sit inside the
    /// range of types the link knows, so a range check alone would still let them through, and a stray old test plugin
    /// would be read as a current one.</para>
    /// </summary>
    private static string? PluginLinkRefusesRetiredMessageTypes()
    {
        var instance = PluginBridgeProtocol.InstanceHash(Guid.NewGuid());
        var packet = new byte[PluginBridgeProtocol.HeaderSize];
        foreach (var type in Enum.GetValues<PluginBridgeMessage>())
        {
            PluginBridgeProtocol.WriteHeader(packet, type, instance, null, 0);
            Check(PluginBridgeProtocol.TryReadHeader(packet, out var read, out _, out _, out _) && read == type,
                $"every message type this build speaks must still parse ({type})");
        }
        foreach (var retired in new byte[] { 2, 5 })
        {
            PluginBridgeProtocol.WriteHeader(packet, PluginBridgeMessage.Hello, instance, null, 0);
            packet[5] = retired;
            Check(!PluginBridgeProtocol.TryReadHeader(packet, out _, out _, out _, out _),
                $"message type {retired}, spoken only by the plugin's test builds, must be refused");
        }
        return $"all {Enum.GetValues<PluginBridgeMessage>().Length} current message types parse; types 2 and 5 from the test builds are refused";
    }

    /// <summary>
    /// A REQUEST WITHOUT AN ASK NUMBER GETS NO AUDIO.
    ///
    /// <para>Only the test builds asked for audio without numbering the ask, and the app answered them with the reply
    /// type that went with them. A request without the number is refused now, so it claims nobody and gets nothing
    /// back.</para>
    /// </summary>
    private static string? PluginLinkRequestNeedsAnAskNumber()
    {
        IPAddress[] peers = [IPAddress.Parse("192.168.1.50"), IPAddress.Parse("192.168.1.51")];
        var buffer = new byte[PluginBridgeProtocol.ClaimSetSizeWithAsk(peers.Length)];
        var length = PluginBridgeProtocol.WriteClaimSet(buffer, 256, peers, 77);
        var whole = PluginBridgeProtocol.TryReadClaimSet(buffer.AsSpan(0, length), out var frames, out var addresses, out var ask);
        Check(whole && frames == 256 && addresses.Length == 8 && ask == 77,
            "this build's request must read back whole: the frame count, both addresses and the ask number");
        Check(!PluginBridgeProtocol.TryReadClaimSet(buffer.AsSpan(0, length - sizeof(int)), out _, out _, out _),
            "a request without an ask number must be refused — only the plugin's test builds sent one");
        return "a numbered request reads back whole; one without the number is refused";
    }

    /// <summary>
    /// A HELLO WITHOUT A PROCESS ID GETS NO REPLY.
    ///
    /// <para>Every current plugin says which DAW process it lives in. Only the test builds said hello without it, and the
    /// app kept each of those in a group of its own. Such a hello is ignored now: no peer list back, and nothing
    /// registered.</para>
    /// </summary>
    private static string? PluginLinkIgnoresAHelloWithoutAProcessId()
    {
        using var host = new PluginBridgeHost(null, port: 0);
        host.PeerListSource = () => [(IPAddress.Parse("192.168.1.50"), "Andre")];
        using var plugin = new PluginBridgeLink(0);
        var hostEnd = new IPEndPoint(IPAddress.Loopback, host.Port);
        var instance = PluginBridgeProtocol.InstanceHash(Guid.NewGuid());
        var lists = 0;
        plugin.MessageReceived += (type, _, _, _, _) => { if (type == PluginBridgeMessage.PeerList) Interlocked.Increment(ref lists); };

        Check(plugin.Send(hostEnd, PluginBridgeMessage.Hello, instance, null, ReadOnlySpan<byte>.Empty), "the hello without a process id must send");
        Thread.Sleep(600);
        Check(Volatile.Read(ref lists) == 0, "a hello without a process id must get no reply — only the plugin's test builds sent one");

        var pid = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(pid, Environment.ProcessId);
        Check(plugin.Send(hostEnd, PluginBridgeMessage.Hello, instance, null, pid), "the current hello must send");
        Check(WaitUntil(() => Volatile.Read(ref lists) > 0, 3000),
            "a hello carrying the process id must still get the peer list, or the silence above proves nothing");
        return "a hello without a process id gets no reply; one carrying it gets the peer list";
    }
}
