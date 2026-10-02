using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace RemSound.Core;

/// <summary>
/// Asks a server what it is BEFORE connecting to it (Ed, 2026-10-02: "it should give you the warning instantly and not
/// connect"). It says hello under a throwaway identity, in a group of its own - so nobody on the server is shown it -
/// proves its address as every member does, pings, and listens for the server's status, which carries its version
/// (server-v2.16 on). An older server answers the hello (an address check, or a member list) but never pings back or sends
/// a status; that shows inside the same short wait. It leaves with a goodbye. No sound, and nothing sent but these few
/// small packets, from a socket of its own.
/// </summary>
public static class ServerVersionProbe
{
    /// <param name="Answered">The server answered at all: an address check, a member list, a ping or its status.</param>
    /// <param name="Version">What it says it is, or null (an older server, or none that answered).</param>
    /// <param name="RoundTripMs">The first ping's round trip, when it pinged back.</param>
    /// <param name="Full">It said it has no room.</param>
    public sealed record Result(bool Answered, string? Version, double? RoundTripMs, bool Full);

    /// <summary>The name the throwaway identity says hello with: what the server's log shows for it.</summary>
    public const string Name = "server check";

    /// <summary>How long, after a server first answers, its silence about its version is waited out: the server sends its
    /// status on its next half-second tick once the address is proved, and answers pings at once.</summary>
    public static readonly TimeSpan OlderAfter = TimeSpan.FromMilliseconds(800);

    /// <summary>With pings coming back (server-v2.16 or later) but no status yet, how much longer the status is waited for.</summary>
    public static readonly TimeSpan StatusWaitsUpTo = TimeSpan.FromSeconds(2);

    public static async Task<Result> ProbeAsync(IPEndPoint server, TimeSpan? limit = null, CancellationToken token = default)
    {
        var until = limit ?? TimeSpan.FromSeconds(3);
        using var udp = new UdpClient(server.AddressFamily);
        var id = RandomNumberGenerator.GetBytes(RelayGroupClient.ClientIdSize);
        var hello = Hello(id, RandomNumberGenerator.GetBytes(RelayGroupClient.GroupTagBytes));
        var clock = Stopwatch.StartNew();
        TimeSpan? answeredAt = null, pingedBackAt = null;
        double? roundTrip = null;
        string? version = null;
        var full = false;
        var nextHello = TimeSpan.Zero;
        var nextPing = TimeSpan.Zero;
        Task<UdpReceiveResult>? pending = null;
        try
        {
            while (clock.Elapsed < until && !token.IsCancellationRequested)
            {
                var now = clock.Elapsed;
                if (version is not null || full) break;
                if (answeredAt is { } a && pingedBackAt is null && now - a >= OlderAfter) break;        // an older server
                if (pingedBackAt is { } p && now - p >= StatusWaitsUpTo) break;                        // pings, no status
                if (now >= nextHello)
                {
                    await SendAsync(udp, hello, server).ConfigureAwait(false);
                    nextHello = now + TimeSpan.FromMilliseconds(answeredAt is null ? 400 : 1000);
                }
                if (answeredAt is not null && now >= nextPing)
                {
                    await SendAsync(udp, Ping(id, clock.Elapsed.Ticks), server).ConfigureAwait(false);
                    nextPing = now + TimeSpan.FromMilliseconds(100);
                }
                pending ??= udp.ReceiveAsync(token).AsTask();
                if (await Task.WhenAny(pending, Task.Delay(25, token)).ConfigureAwait(false) != pending) continue;
                UdpReceiveResult got;
                try { got = await pending.ConfigureAwait(false); }
                catch (SocketException) { pending = null; continue; }     // a refusal from a closed port: keep listening
                pending = null;
                if (!got.RemoteEndPoint.Equals(server)) continue;
                var data = got.Buffer;
                if (data.Length < RemPacket.HeaderSize) continue;
                var type = data[5];
                if (type == (byte)RemPacketType.AddrCheck)
                {
                    answeredAt ??= clock.Elapsed;
                    await SendAsync(udp, data, server).ConfigureAwait(false);   // echoed exactly as it came, as every member does
                }
                else if (type is RelayGroupClient.TypeRoster)
                {
                    answeredAt ??= clock.Elapsed;
                }
                else if (type is RelayGroupClient.TypeFull)
                {
                    answeredAt ??= clock.Elapsed;
                    full = true;
                }
                else if (type == RelayGroupClient.TypeServerPing && data.Length >= RelayGroupClient.GroupHeaderSize + 8)
                {
                    answeredAt ??= clock.Elapsed;
                    pingedBackAt ??= clock.Elapsed;
                    var sentTicks = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(RelayGroupClient.GroupHeaderSize));
                    roundTrip ??= TimeSpan.FromTicks(clock.Elapsed.Ticks - sentTicks).TotalMilliseconds;
                }
                else if (type == RelayGroupClient.TypeServerInfo && data.Length > RelayGroupClient.GroupHeaderSize)
                {
                    answeredAt ??= clock.Elapsed;
                    if (RelayGroupClient.ReadServerInfo(data.AsSpan(RelayGroupClient.GroupHeaderSize), DateTime.UtcNow) is { } info)
                        version = info.Version;
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            // Goodbye, so the server forgets the throwaway identity at once rather than after a minute.
            var bye = Header(RelayGroupClient.TypeBye, id, 0);
            for (var i = 0; i < 2; i++)
            {
                try { udp.Send(bye, bye.Length, server); } catch (SocketException) { break; } catch (ObjectDisposedException) { break; }
            }
        }
        return new Result(answeredAt is not null, version, roundTrip, full);
    }

    private static async Task SendAsync(UdpClient udp, byte[] packet, IPEndPoint to)
    {
        try { await udp.SendAsync(packet, packet.Length, to).ConfigureAwait(false); }
        catch (SocketException) { /* nothing there: the wait decides */ }
    }

    private static byte[] Header(byte type, byte[] id, int extra)
    {
        var packet = new byte[RelayGroupClient.GroupHeaderSize + extra];
        RemPacket.WriteHeader(packet, (RemPacketType)type, 0, 0);
        packet[4] = RelayGroupClient.GroupVersion;
        id.CopyTo(packet.AsSpan(RemPacket.HeaderSize));
        return packet;
    }

    /// <summary>A hello as the app's: the name, a group tag nobody else has, and an empty list of people ticked.</summary>
    private static byte[] Hello(byte[] id, byte[] group)
    {
        var packet = Header(RelayGroupClient.TypeHello, id, RelayGroupClient.NameBytes + RelayGroupClient.GroupTagBytes + 1);
        RelayGroupClient.EncodeName(Name, packet.AsSpan(RelayGroupClient.GroupHeaderSize, RelayGroupClient.NameBytes));
        group.CopyTo(packet.AsSpan(RelayGroupClient.GroupHeaderSize + RelayGroupClient.NameBytes));
        return packet;
    }

    private static byte[] Ping(byte[] id, long ticks)
    {
        var packet = Header(RelayGroupClient.TypeServerPing, id, 8);
        BinaryPrimitives.WriteInt64LittleEndian(packet.AsSpan(RelayGroupClient.GroupHeaderSize), ticks);
        return packet;
    }
}
