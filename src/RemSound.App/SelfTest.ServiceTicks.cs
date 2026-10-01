using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// The lock-screen service sends to the people ticked in its profile, and to nobody else.
/// </summary>
internal static partial class SelfTest
{
    /// <summary>
    /// THE SERVICE SENDS ONLY TO THE PEOPLE TICKED: NOBODY TICKED IS NOBODY.
    ///
    /// <para>Review 2026-09-25, Ed agreed the fix. Unticking every peer in the service profile made the service send to
    /// all of them: an empty tick list was read as "everyone in the list". And the window showed them all ticked again
    /// when it was reopened, so the choice could not even be seen, and pressing OK saved them all ticked. The main app
    /// has always sent to nobody when nobody is ticked. The window is driven here through the control channel, the way
    /// --control drives a headless copy.</para>
    /// </summary>
    private static string? ServiceSendsOnlyToThePeopleTicked()
    {
        const string A = "192.0.2.41", B = "192.0.2.42";
        const string PeerList = "\"Peers to send to\"";

        // The host: nobody ticked is nobody, however many are listed; one ticked is that one.
        var nobody = Profile.NewBlank();
        nobody.RememberedPeers = [A, B];
        nobody.SelectedConnectedPeers = [];
        var none = ServiceSendHost.BuildEndpoints(nobody);
        Check(none.Count == 0,
            $"THE LEAK: with nobody ticked the service must send to nobody, not to everyone in its list ({string.Join(", ", none)})");
        var justB = Profile.NewBlank();
        justB.RememberedPeers = [A, B];
        justB.SelectedConnectedPeers = [B];
        var one = ServiceSendHost.BuildEndpoints(justB);
        Check(one.Count == 1 && one[0].Address.ToString() == B, $"with one ticked it must send to that one only ({string.Join(", ", one)})");

        // And its log says why it sends nothing: with nobody at the screen, the log is the only way to find out.
        var lines = new ConcurrentQueue<string>();
        using (var host = new ServiceSendHost(() => nobody, lines.Enqueue))
            Check(!host.ApplyProfile(nobody), "with nobody ticked the service must not start sending");
        Check(lines.Any(l => l.Contains("nobody is ticked in the service profile", StringComparison.Ordinal)),
            $"the service log must say nobody is ticked (it said: {string.Join(" | ", lines)})");

        // The window: untick both through the control channel, and what it saves ticks nobody but still lists both.
        var both = Profile.NewBlank();
        both.RememberedPeers = [A, B];
        both.SelectedConnectedPeers = [A, B];
        Profile saved;
        using (var dialog = new ServiceProfileDialog(both, false))
        {
            _ = dialog.Handle;
            var engine = new RemoteControlEngine(() => dialog);
            foreach (var peer in new[] { A, B })
            {
                var said = engine.Execute($"check {PeerList} {peer} off");
                Check(said.StartsWith("ok", StringComparison.Ordinal), $"the service's peer list must be drivable from the control channel ({said})");
            }
            saved = dialog.ResultForTest;
        }
        Check(saved.SelectedConnectedPeers.Count == 0 && saved.RememberedPeers.Count == 2,
            $"unticking both must save nobody ticked, with both still listed ({saved.SelectedConnectedPeers.Count} ticked, {saved.RememberedPeers.Count} listed)");
        Check(ServiceSendHost.BuildEndpoints(saved).Count == 0, "and the service must send what was saved: to nobody");

        // Reopened, it must show nobody ticked. It showed everybody, and pressing OK then saved them all ticked again.
        using (var reopened = new ServiceProfileDialog(saved, false))
        {
            _ = reopened.Handle;
            var engine = new RemoteControlEngine(() => reopened);
            var got = engine.Execute($"get {PeerList}");
            Check(got.Contains("2 items, 0 ticked", StringComparison.Ordinal),
                $"THE HIDDEN CHOICE: reopened, the window must show nobody ticked, not everybody (get said: {got.Replace('\n', ' ')})");
            Check(reopened.ResultForTest.SelectedConnectedPeers.Count == 0, "and pressing OK must keep nobody ticked");
        }
        using (var partly = new ServiceProfileDialog(justB, false))
        {
            _ = partly.Handle;
            var got = new RemoteControlEngine(() => partly).Execute($"get {PeerList}");
            Check(got.Contains($"\"{A}\" not ticked", StringComparison.Ordinal) && got.Contains($"\"{B}\" ticked", StringComparison.Ordinal),
                $"a profile with some ticked must show exactly those (get said: {got.Replace('\n', ' ')})");
        }
        return "with nobody ticked the service sends to nobody and its log says so; one ticked is that one; the window, driven "
             + "through the control channel, saves nobody ticked and shows it so when reopened";
    }

    /// <summary>
    /// THE SERVICE STOPS SENDING TO A PEER THAT STOPS ANSWERING, AND STARTS AGAIN WHEN IT ANSWERS.
    ///
    /// <para>Issues #8 and #15: never stream into a dead address, and pick a peer up again the moment it comes back. The
    /// rule itself (PeerArming) is pinned on made-up health figures by "Service reachability-gated sending"; nothing proved
    /// the running host applies it (review 2026-09-28). So this runs the real service loop, capture and heartbeat against
    /// two peers on loopback addresses of their own, each answering pings as a RemSound does, and judges by the audio that
    /// reaches them. One stops answering: while it has been unreachable for less than the 30 s grace it is still sent to;
    /// past it, its audio stops and the other's carries on; and when it answers again its audio comes back. The grace
    /// passes on the heartbeat's own clock (its UtcNowForTest seam), moved on here rather than waited out, and the loop's
    /// turns are counted rather than timed.</para>
    /// </summary>
    private static string? TheServiceDropsAndRearmsAPeer()
    {
        string? deviceId;
        try { deviceId = AudioDeviceCatalog.LoadOutputs().FirstOrDefault(o => o.DeviceId is not null)?.DeviceId; }
        catch (Exception ex) { return Skip("could not enumerate outputs: " + ex.Message); }
        if (deviceId is null) return Skip("no usable output device to capture from");

        // Two addresses, not two ports on one: the heartbeat credits a pong to whoever has the address it came from.
        using var steady = new StandInPeer(IPAddress.Parse("127.0.0.4"));
        using var fading = new StandInPeer(IPAddress.Parse("127.0.0.5"));
        var profile = new Profile
        {
            Title = "selftest-service-arming",
            WasapiSendMode = "devices",
            Password = RemSoundCrypto.Obfuscate("selftest-service-arming"),
        };
        profile.SelectedWasapiSendOutputs.Add(deviceId);
        profile.SelectedConnectedPeers.Add(steady.Entry);
        profile.SelectedConnectedPeers.Add(fading.Entry);

        var restorePresencePort = ServiceSendHost.PresencePortForTest;
        ServiceSendHost.PresencePortForTest = FreeUdpPort();
        var lines = new ConcurrentQueue<string>();
        using var cts = new CancellationTokenSource();
        Thread? loop = null;
        var host = new ServiceSendHost(() => profile, lines.Enqueue);
        var turns = 0L;
        host.LoopTurnedForTest = () => Interlocked.Increment(ref turns);
        // Two whole turns of the loop after this point: it has re-armed, or not, on what the heartbeat says now.
        void TwoTurns()
        {
            var from = Interlocked.Read(ref turns);
            Check(WaitUntil(() => Interlocked.Read(ref turns) >= from + 2, 3000), "premise: the service loop must keep turning");
        }
        // Whether a peer goes on getting the service's audio, judged over the next 20 packets the steady peer receives.
        bool KeepsReceiving(StandInPeer peer)
        {
            var from = peer.Audio;
            var steadyFrom = steady.Audio;
            Check(WaitUntil(() => steady.Audio >= steadyFrom + 20, 3000),
                $"premise: the peer still answering must go on receiving the service's audio ({steady.Audio - steadyFrom} packets)");
            return peer.Audio > from;
        }
        try
        {
            var token = @"Global\RemSound.Interactive.selftest." + Guid.NewGuid().ToString("N");
            loop = new Thread(() => host.RunLoopWithToken(cts.Token, token, pollMs: 20, resumeSettleMs: 0)) { IsBackground = true };
            loop.Start();
            Check(WaitUntil(() => steady.Audio > 0 && fading.Audio > 0, 5000),
                $"premise: the service must send to both peers at first (it said: {string.Join(" | ", lines.TakeLast(4))})");
            var heartbeat = Require(host.PresenceForTest.HeartbeatForTest, "the running service must have a heartbeat");
            PeerHealthState StateOf(StandInPeer peer) =>
                heartbeat.GetAllPeerHealth().FirstOrDefault(h => h.AudioEndpoint.Equals(peer.Endpoint))?.State ?? PeerHealthState.Unknown;
            Check(WaitUntil(() => StateOf(steady) == PeerHealthState.Healthy && StateOf(fading) == PeerHealthState.Healthy, 5000),
                $"premise: both peers must answer the service's pings and read as reachable ({StateOf(steady)}, {StateOf(fading)})");

            // From here the health windows are judged on a clock that stands still unless moved on here. One peer goes quiet.
            // The other answers its latest ping again each time the clock moves - a pong carrying a stamp the service sent
            // counts whenever it comes - so its last answer is the clock's now, and it never looks dead.
            var utc = DateTime.UtcNow.Ticks;
            heartbeat.UtcNowForTest = () => new DateTime(Interlocked.Read(ref utc), DateTimeKind.Utc);
            fading.Answering = false;
            void QuietFor(TimeSpan more)
            {
                Interlocked.Add(ref utc, more.Ticks);
                Check(steady.AnswerAgain(), "premise: the peer still answering must have been pinged");
                Check(WaitUntil(() => StateOf(steady) == PeerHealthState.Healthy, 3000),
                    $"premise: the peer still answering must read as reachable ({StateOf(steady)})");
                TwoTurns();
            }

            // Quiet for 20 s: unreachable, but inside the grace - still sent to, in case it is a blip.
            QuietFor(TimeSpan.FromSeconds(20));
            Check(StateOf(fading) == PeerHealthState.Unreachable, $"premise: quiet for 20 s, a peer must read as unreachable ({StateOf(fading)})");
            Check(KeepsReceiving(fading),
                "THE GRACE: a peer unreachable for less than 30 s must still be sent to - a moment's loss must not cut the stream");

            // Quiet for 40 s: past the grace. Its audio stops, and the other's carries on.
            QuietFor(TimeSpan.FromSeconds(20));
            Check(!KeepsReceiving(fading),
                $"THE DEAD ADDRESS: a peer unreachable for longer than 30 s must no longer be sent audio ({lines.LastOrDefault(l => l.Contains("reachable peer", StringComparison.Ordinal)) ?? "the log never said"})");
            Check(KeepsReceiving(steady), "and the peer still answering must go on being sent to");

            // It answers again - its latest ping, for it was never stopped being pinged - and is sent to again at once.
            fading.Answering = true;
            Check(fading.AnswerAgain(), "premise: the peer that went quiet must still have been pinged all along");
            Check(WaitUntil(() => StateOf(fading) == PeerHealthState.Healthy, 3000),
                $"premise: a peer that answers again must read as reachable again ({StateOf(fading)})");
            TwoTurns();
            Check(KeepsReceiving(fading), "THE RETURN: a peer that answers again must be sent audio again");
            var said = lines.ToList();
            var dropped = said.FindIndex(l => l.Contains("streaming to 1/2 reachable peer(s)", StringComparison.Ordinal));
            Check(dropped >= 0 && said.Skip(dropped).Any(l => l.Contains("streaming to 2/2 reachable peer(s)", StringComparison.Ordinal)),
                $"THE LOG: dropping a peer, and then taking it back, must each be logged ({string.Join(" | ", said.Where(l => l.Contains("reachable peer", StringComparison.Ordinal)))})");
            return "the running service kept sending to a peer quiet for 20 s (inside the grace), stopped at 40 s while the other "
                 + "carried on, and sent to it again the moment it answered; the log says both";
        }
        finally
        {
            cts.Cancel();
            loop?.Join(3000);
            host.Dispose();
            ServiceSendHost.PresencePortForTest = restorePresencePort;
        }
    }

    /// <summary>A RemSound peer on a loopback address of its own, as far as a sender can tell: it answers every heartbeat ping
    /// with a pong carrying the ping's own stamp (while <see cref="Answering"/>), and counts the audio that reaches it.</summary>
    private sealed class StandInPeer : IDisposable
    {
        private readonly UdpClient socket;
        private readonly Thread listener;
        private volatile bool closed;
        private long audio;
        private long pongs;
        private (long Tick, IPEndPoint From)? lastPing;

        /// <summary>False: hear everything and answer no ping, as a peer that has gone away.</summary>
        public volatile bool Answering = true;

        public IPEndPoint Endpoint { get; }
        public string Entry => $"{Endpoint.Address}:{Endpoint.Port}";
        public long Audio => Interlocked.Read(ref audio);
        public long Pongs => Interlocked.Read(ref pongs);

        public StandInPeer(IPAddress address)
        {
            socket = new UdpClient(new IPEndPoint(address, 0));
            Endpoint = (IPEndPoint)socket.Client.LocalEndPoint!;
            listener = new Thread(Listen) { IsBackground = true, Name = $"gate: stand-in peer {Endpoint}" };
            listener.Start();
        }

        private void Listen()
        {
            while (!closed)
            {
                var from = new IPEndPoint(IPAddress.Any, 0);
                byte[] data;
                try { data = socket.Receive(ref from); }
                catch (SocketException) { if (closed) return; continue; }   // the close, or a reset from a port that has gone
                catch (ObjectDisposedException) { return; }
                if (!RemPacket.TryReadHeader(data, out var type, out _, out _)) continue;
                if (type == RemPacketType.Audio) Interlocked.Increment(ref audio);
                if (type != RemPacketType.Heartbeat || !Answering) continue;
                if (!RemPacket.TryReadHeartbeat(data.AsSpan(RemPacket.HeaderSize), out var kind, out var tick) || kind != HeartbeatKind.Ping) continue;
                lock (socket) lastPing = (tick, from);
                Answer(tick, from);
            }
        }

        /// <summary>Answer the latest ping again, now. False if none has come.</summary>
        public bool AnswerAgain()
        {
            (long Tick, IPEndPoint From)? ping;
            lock (socket) ping = lastPing;
            if (ping is not { } p) return false;
            Answer(p.Tick, p.From);
            return true;
        }

        private void Answer(long tick, IPEndPoint to)
        {
            var pong = new byte[RemPacket.HeaderSize + RemPacket.HeartbeatPayloadSize];
            RemPacket.WriteHeader(pong, RemPacketType.Heartbeat, 0xFFFF, 1);
            RemPacket.WriteHeartbeatPayload(pong.AsSpan(RemPacket.HeaderSize), HeartbeatKind.Pong, tick);
            try { socket.Send(pong, pong.Length, to); Interlocked.Increment(ref pongs); }
            catch (SocketException) { /* UDP */ }
            catch (ObjectDisposedException) { }
        }

        public void Dispose()
        {
            closed = true;
            socket.Dispose();
            listener.Join(2000);
        }
    }
}
