using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Concentus;
using Concentus.Enums;
using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// LISTEN IN A WEB BROWSER (2026-10-10, Ed: "on my work computer I can't install RemSound, but I can visit
/// websites"). A small web server on this computer's loopback address only: it serves one page and, to a
/// browser that proves it knows the password, streams what this RemSound is sending to its peers.
///
/// <para>How it reaches a browser at work: Tailscale Funnel gives this computer a public https address on
/// port 443 and passes it down Tailscale's own connection to 127.0.0.1 here, so nothing is opened on the
/// router. Funnel carries ordinary web connections only, so the sound goes over one WebSocket.</para>
///
/// <para>For speed: each sending lane (the WASAPI capture, the ASIO inputs, the DAW track) is its own
/// stream, encoded here in 5 ms Opus frames at the lowest-delay setting, separately from what peers get,
/// and the browser mixes them, so no lane waits on another. A browser that falls behind loses its oldest
/// frames rather than queueing them. The password never crosses the network: the browser answers a fresh
/// challenge with an HMAC keyed by PBKDF2 of the password. It is kept in the settings scrambled, like the
/// profile passwords (Ed, 2026-10-10: the box shows the password, and what's in it is the password).</para>
/// </summary>
internal sealed class WebListener : IDisposable
{
    public const int DefaultPort = 47840;
    public const int DefaultIterations = 150_000;
    /// <summary>5 ms at 48 kHz: the smallest Opus frame that still sounds right at a web bitrate.</summary>
    public const int FrameSamples = 240;
    private const int OpusBitrate = 160_000;
    private const int MaxListeners = 4;
    private const int MaxPending = 8;
    /// <summary>Frames a browser may have waiting before the oldest are dropped: about 160 ms of one stream.</summary>
    private const int ClientQueueFrames = 32;
    private const int FailuresBeforeLockout = 10;
    private static readonly TimeSpan LockoutWindow = TimeSpan.FromMinutes(10);

    private readonly TcpListener listener;
    private readonly Thread acceptThread;
    private readonly Thread worker;
    private readonly Func<bool> isMuted;
    private readonly Action<string> log;
    private readonly object passwordGate = new();
    private string? password;
    private byte[] salt = RandomNumberGenerator.GetBytes(16);
    private byte[]? key;   // PBKDF2 of the password with the salt, worked out at the first answer, not on the UI thread
    private readonly int iterations;
    private readonly byte[] pageBytes;
    private volatile bool running = true;

    private readonly object clientsGate = new();
    private readonly List<Client> clients = [];
    private int pending;
    private volatile int listening;
    private volatile bool anyOpus, anyPcm;

    private readonly object failuresGate = new();
    private readonly Queue<DateTime> failures = new();

    private readonly ConcurrentQueue<Block> queue = new();
    private readonly AutoResetEvent signal = new(false);
    private readonly Dictionary<RenderRoute, Lane> lanes = [];
    private long lastAudioTicks;
    /// <summary>When sound last reached this listener at all, listeners or not: a browser joining while RemSound sends is
    /// not greeted with "not sending anything" (Ed, 2026-10-12, at 12:45).</summary>
    private long lastFedTicks;
    private bool idleSent = true;

    /// <summary>What a wrong password costs, in milliseconds. A seam, so the gate can try the lockout quickly.</summary>
    internal static int WrongPasswordDelayMs = 1000;

    /// <summary>How long without sound before a browser is told nothing is being sent. A seam for the gate.</summary>
    internal int IdleAfterMs { get; set; } = 2000;
    /// <summary>How often every browser is told the connection is alive, sound or not (Ed, 2026-10-12: a page that reconnects
    /// by itself must tell a quiet RemSound from a connection that died without closing). Seam for the self-test.</summary>
    internal int AliveEveryMs { get; set; } = 5000;
    private long lastAliveTicks;

    public int Port { get; }
    public int ListenerCount => listening;

    /// <summary>This computer's name, said to a browser once it's in: the page shows "Receiving from" it (Ed, 2026-10-11).</summary>
    public string SourceName { get; init; } = MachineName();

    private volatile bool autoStart;
    /// <summary>Whether the page starts by itself when it loads (the Web listener window's tick box), said in the challenge
    /// so a page holding a remembered password knows whether to use it.</summary>
    public bool AutoStart { get => autoStart; set => autoStart = value; }

    private static string MachineName()
    {
        try { return System.Net.Dns.GetHostName(); } catch { return Environment.MachineName; }
    }

    private readonly record struct Block(RenderRoute Route, float[] Data, int Length);

    private sealed class Lane
    {
        public readonly float[] Frame = new float[FrameSamples * 2];
        public int Filled;
        public uint Sequence;
        public IOpusEncoder? Opus;
        public readonly byte[] OpusScratch = new byte[4000];
    }

    private sealed class Client
    {
        public Client(WebSocket socket, int number)
        {
            Socket = socket;
            Number = number;
            Outbox = Channel.CreateBounded<(byte[], bool)>(
                new BoundedChannelOptions(ClientQueueFrames) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true },
                _ => Interlocked.Increment(ref Dropped));
        }

        public readonly WebSocket Socket;
        /// <summary>Which browser this is since the listener opened: "browser 3" in the status and the log.</summary>
        public readonly int Number;
        public readonly Channel<(byte[] Data, bool Text)> Outbox;
        public bool Pcm;

        // What the browser and this end can say about the connection (Ed, 2026-10-12: "you probably want a way of reading
        // the buffers via remsound without me having to paste it to you").
        /// <summary>Messages RemSound threw away because this connection could not keep up: a slow upload or a stalled link.</summary>
        public int Dropped;
        public readonly object Gate = new();
        /// <summary>The page's own figures, as it last reported them, already put into words.</summary>
        public string? Report;
        public int Reconnects;
        /// <summary>Round trips of the "alive" message over the last minute: when, and how long.</summary>
        public readonly List<(long At, int Ms)> RoundTrips = [];
        public string? LastLogged;
        public long LastLoggedAt;
        /// <summary>When the browser last said anything, and whether it ever answers at all (a page from before the reports never
        /// does, and is never judged by its silence).</summary>
        public long LastHeardAt = Environment.TickCount64;
        public bool AnswersBack;
    }

    private int browsersJoined;

    /// <summary>A page that answers every 5 s and has said nothing for this long has gone without closing (Ed, 2026-10-12: after
    /// he moved, two such connections stayed listed and kept being sent sound, holding places of the four). Seam for the gate.</summary>
    internal int SilentBrowserMs { get; set; } = 20_000;

    /// <summary>Lets go of every browser that answers back and has gone quiet, freeing its place; logged.</summary>
    private void DropSilentBrowsers()
    {
        Client[] quiet;
        lock (clientsGate)
            quiet = [.. clients.Where(c => c.AnswersBack && Environment.TickCount64 - Volatile.Read(ref c.LastHeardAt) > SilentBrowserMs)];
        foreach (var c in quiet)
        {
            log($"web listener: browser {c.Number} has said nothing for {SilentBrowserMs / 1000} s - its connection is taken as gone");
            try { c.Socket.Abort(); } catch { }
        }
    }


    public WebListener(int port, string? password, int iterations, Func<bool> isMuted, Action<string> log)
    {
        this.isMuted = isMuted;
        this.log = log;
        this.iterations = iterations > 0 ? iterations : DefaultIterations;
        this.password = string.IsNullOrEmpty(password) ? null : password;
        pageBytes = LoadPage();
        listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "remsound-web-accept" };
        worker = new Thread(WorkLoop) { IsBackground = true, Name = "remsound-web-encode", Priority = ThreadPriority.AboveNormal };
        acceptThread.Start();
        worker.Start();
        log($"web listener: open on 127.0.0.1:{Port}" + (this.password is null ? " - no password is set yet, so every browser is refused" : ""));
    }

    /// <summary>Whether a password has been set, so a browser can be let in at all.</summary>
    public bool HasPassword { get { lock (passwordGate) return password is not null; } }

    /// <summary>A new password, in force at once: a fresh salt, and every browser listening is disconnected, so only
    /// someone with the new one comes back. The same password again changes nothing.</summary>
    public void SetPassword(string? newPassword)
    {
        if (string.IsNullOrEmpty(newPassword)) newPassword = null;
        lock (passwordGate)
        {
            if (newPassword == password) return;
            password = newPassword;
            salt = RandomNumberGenerator.GetBytes(16);
            key = null;
        }
        List<Client> all;
        lock (clientsGate) { all = [.. clients]; clients.Clear(); }
        foreach (var c in all) { c.Outbox.Writer.TryComplete(); try { c.Socket.Abort(); } catch { } }
        RecountCodecs();
        log("web listener: the password changed" + (all.Count > 0 ? $" - {all.Count} browser(s) disconnected" : "")
            + (newPassword is null ? "; with none set, every browser is refused" : ""));
    }

    /// <summary>The salt for a challenge and the key it is checked against, the key worked out on first use.</summary>
    private (byte[] Salt, byte[]? Key) Credentials()
    {
        lock (passwordGate)
        {
            if (password is null) return (salt, null);
            key ??= Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);
            return (salt, key);
        }
    }

    private static byte[] LoadPage()
    {
        using var stream = typeof(WebListener).Assembly.GetManifestResourceStream("weblisten-index.html");
        if (stream is null) return Encoding.UTF8.GetBytes("<!doctype html><title>RemSound listener</title><p>The listening page is missing from this build.</p>");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    // ------- the audio tap (audio threads) --------------------------------------------------------------

    /// <summary>From <c>AudioSender.OnWebSamples</c>: copied and handed to the encoding thread. Costs one
    /// volatile read when nobody is listening.</summary>
    public void Feed(ReadOnlyMemory<float> samples, RenderRoute route)
    {
        if (samples.IsEmpty) return;
        Volatile.Write(ref lastFedTicks, Environment.TickCount64);
        if (listening == 0) return;
        if (isMuted()) return;
        if (queue.Count > 2000) return;   // the encoder has stalled: never let the audio thread pile up memory
        var copy = ArrayPool<float>.Shared.Rent(samples.Length);
        samples.Span.CopyTo(copy);
        queue.Enqueue(new Block(route, copy, samples.Length));
        signal.Set();
    }

    // ------- encoding and sending (worker thread) -------------------------------------------------------

    private void WorkLoop()
    {
        while (running)
        {
            signal.WaitOne(50);
            while (queue.TryDequeue(out var block))
            {
                try { Encode(block); }
                catch (Exception ex) { log($"web listener: encoding failed ({ex.GetType().Name}: {ex.Message})"); }
                finally { ArrayPool<float>.Shared.Return(block.Data); }
                lastAudioTicks = Environment.TickCount64;
                if (idleSent) { idleSent = false; BroadcastText("{\"type\":\"active\"}"); }
            }
            DropSilentBrowsers();
            if (listening > 0 && Environment.TickCount64 - lastAliveTicks > AliveEveryMs)
            {
                lastAliveTicks = Environment.TickCount64;
                // Carries this end's clock: the page sends it straight back, and the difference is the round trip.
                BroadcastText($"{{\"type\":\"alive\",\"t\":{lastAliveTicks}}}");
                LogBrowsers();
            }
            if (!idleSent && listening > 0 && Environment.TickCount64 - lastAudioTicks > IdleAfterMs)
            {
                idleSent = true;
                // A lane that stopped mid-frame starts clean when it comes back.
                foreach (var lane in lanes.Values) lane.Filled = 0;
                BroadcastText("{\"type\":\"idle\"}");
            }
        }
    }

    private void Encode(Block block)
    {
        if (!lanes.TryGetValue(block.Route, out var lane)) lanes[block.Route] = lane = new Lane();
        var src = block.Data.AsSpan(0, block.Length);
        while (!src.IsEmpty)
        {
            var take = Math.Min(src.Length, lane.Frame.Length - lane.Filled);
            src[..take].CopyTo(lane.Frame.AsSpan(lane.Filled));
            lane.Filled += take;
            src = src[take..];
            if (lane.Filled < lane.Frame.Length) break;
            lane.Filled = 0;
            var seq = lane.Sequence++;
            if (anyOpus)
            {
                lane.Opus ??= NewEncoder();
                var n = lane.Opus.Encode(lane.Frame, FrameSamples, lane.OpusScratch, lane.OpusScratch.Length);
                Broadcast(Message((byte)block.Route, seq, lane.OpusScratch.AsSpan(0, n)), pcm: false);
            }
            if (anyPcm)
            {
                var pcm = new byte[FrameSamples * 4];
                for (var i = 0; i < lane.Frame.Length; i++)
                    BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), (short)Math.Round(Math.Clamp(lane.Frame[i], -1f, 1f) * 32767f));
                Broadcast(Message((byte)block.Route, seq, pcm), pcm: true);
            }
        }
    }

    private static IOpusEncoder NewEncoder()
    {
        var e = OpusCodecFactory.CreateEncoder(48000, 2, OpusApplication.OPUS_APPLICATION_RESTRICTED_LOWDELAY, TextWriter.Null);
        e.Bitrate = OpusBitrate;
        e.Complexity = 8;
        e.UseVBR = true;
        // The WebSocket loses nothing, so there is no loss to correct for.
        e.UseInbandFEC = false;
        e.PacketLossPercent = 0;
        return e;
    }

    /// <summary>[1][stream][sequence, u32 little-endian][payload]</summary>
    internal static byte[] Message(byte stream, uint sequence, ReadOnlySpan<byte> payload)
    {
        var m = new byte[6 + payload.Length];
        m[0] = 1;
        m[1] = stream;
        BinaryPrimitives.WriteUInt32LittleEndian(m.AsSpan(2), sequence);
        payload.CopyTo(m.AsSpan(6));
        return m;
    }

    private void Broadcast(byte[] message, bool pcm)
    {
        lock (clientsGate)
            foreach (var c in clients)
                if (c.Pcm == pcm) c.Outbox.Writer.TryWrite((message, false));
    }

    private void BroadcastText(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        lock (clientsGate)
            foreach (var c in clients) c.Outbox.Writer.TryWrite((bytes, true));
    }

    private void RecountCodecs()
    {
        lock (clientsGate)
        {
            anyOpus = clients.Exists(c => !c.Pcm);
            anyPcm = clients.Exists(c => c.Pcm);
            listening = clients.Count;
        }
    }

    // ------- connections ---------------------------------------------------------------------------------

    private void AcceptLoop()
    {
        while (running)
        {
            TcpClient tcp;
            try { tcp = listener.AcceptTcpClient(); }
            catch { if (!running) return; continue; }
            if (Interlocked.Increment(ref pending) > MaxPending)
            {
                Interlocked.Decrement(ref pending);
                tcp.Dispose();
                continue;
            }
            tcp.NoDelay = true;
            _ = Task.Run(async () =>
            {
                try { await Serve(tcp); }
                catch { /* a connection that goes wrong is that connection's problem only */ }
                finally { Interlocked.Decrement(ref pending); tcp.Dispose(); }
            });
        }
    }

    private async Task Serve(TcpClient tcp)
    {
        var stream = tcp.GetStream();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (path, headers) = await ReadRequest(stream, timeout.Token);
        if (path is null) return;
        if (headers.TryGetValue("upgrade", out var upgrade) && upgrade.Equals("websocket", StringComparison.OrdinalIgnoreCase))
        {
            if (path != "/listen" || !headers.TryGetValue("sec-websocket-key", out var wsKey)) { await Respond(stream, "404 Not Found", "text/plain", "Not found"u8.ToArray()); return; }
            var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(wsKey.Trim() + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + accept + "\r\n\r\n"));
            using var socket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true, KeepAliveInterval = TimeSpan.FromSeconds(15) });
            await Listen(socket);
            return;
        }
        if (path is "/" or "/index.html")
            await Respond(stream, "200 OK", "text/html; charset=utf-8", pageBytes,
                "Content-Security-Policy: default-src 'none'; script-src 'unsafe-inline' blob:; style-src 'unsafe-inline'; connect-src 'self'; worker-src blob:\r\n");
        else
            await Respond(stream, "404 Not Found", "text/plain", "Not found"u8.ToArray());
    }

    private static async Task<(string? Path, Dictionary<string, string> Headers)> ReadRequest(NetworkStream stream, CancellationToken ct)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var buffer = new byte[8192];
        var used = 0;
        while (true)
        {
            if (used == buffer.Length) return (null, headers);
            var n = await stream.ReadAsync(buffer.AsMemory(used), ct);
            if (n == 0) return (null, headers);
            used += n;
            var end = buffer.AsSpan(0, used).IndexOf("\r\n\r\n"u8);
            if (end < 0) continue;
            var lines = Encoding.ASCII.GetString(buffer, 0, end).Split("\r\n");
            var first = lines[0].Split(' ');
            if (first.Length < 2 || first[0] != "GET") return (null, headers);
            foreach (var line in lines.Skip(1))
            {
                var colon = line.IndexOf(':');
                if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
            var path = first[1];
            var q = path.IndexOf('?');
            return (q >= 0 ? path[..q] : path, headers);
        }
    }

    private static async Task Respond(NetworkStream stream, string status, string type, byte[] body, string extraHeaders = "")
    {
        var head = $"HTTP/1.1 {status}\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\n" +
                   $"X-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer\r\n{extraHeaders}Connection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
        await stream.WriteAsync(body);
    }

    private async Task Listen(WebSocket socket)
    {
        // The challenge: a fresh nonce, answered with HMAC-SHA256 keyed by PBKDF2 of the password.
        var nonce = RandomNumberGenerator.GetBytes(32);
        var (challengeSalt, _) = Credentials();
        await SendText(socket, JsonSerializer.Serialize(new
        {
            type = "challenge",
            salt = Convert.ToBase64String(challengeSalt),
            iterations,
            nonce = Convert.ToBase64String(nonce),
            autoStart,
        }));
        using var authTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        // The answer is the message that says it is one ("type":"auth"). A page reconnecting by itself may send its report or
        // a reply to an alive before its answer is ready (working the answer out takes it a moment, on purpose); those are
        // passed over, not taken as a wrong password (Ed, 2026-10-12: refused at 12:36 that way, and it then forgot his
        // saved password). An answer without a type, from an older page, still counts.
        string? reply;
        while (true)
        {
            reply = await ReceiveText(socket, authTimeout.Token);
            if (reply is null) return;
            if (!IsOtherThanAnAnswer(reply)) break;
        }
        string? response = null, codec = null;
        try
        {
            using var doc = JsonDocument.Parse(reply);
            response = doc.RootElement.GetProperty("response").GetString();
            codec = doc.RootElement.TryGetProperty("codec", out var c) ? c.GetString() : "opus";
        }
        catch { /* treated as a wrong answer below */ }

        var refusal = CheckAnswer(nonce, response, challengeSalt);
        if (refusal is not null)
        {
            await SendText(socket, JsonSerializer.Serialize(new { type = "refused", reason = refusal }));
            await CloseQuietly(socket);
            return;
        }

        var client = new Client(socket, Interlocked.Increment(ref browsersJoined)) { Pcm = codec == "pcm16" };
        bool room;
        lock (clientsGate)
        {
            room = clients.Count < MaxListeners;
            if (room) clients.Add(client);
        }
        if (!room)
        {
            // Full for now, not wrong: retry tells a page that reconnects by itself to keep trying (a place comes free).
            await SendText(socket, JsonSerializer.Serialize(new { type = "refused", reason = $"{MaxListeners} browsers are already listening.", retry = true }));
            await CloseQuietly(socket);
            return;
        }
        RecountCodecs();
        log($"web listener: a browser is listening (browser {client.Number}; {ListenerCount} now, {(client.Pcm ? "16-bit PCM" : "Opus")})");
        await SendText(socket, JsonSerializer.Serialize(new { type = "welcome", frameMs = FrameSamples * 1000 / 48000, source = SourceName, autoStart }));
        if (idleSent && Environment.TickCount64 - Volatile.Read(ref lastFedTicks) > IdleAfterMs) await SendText(socket, "{\"type\":\"idle\"}");

        using var gone = new CancellationTokenSource();
        var sending = Task.Run(async () =>
        {
            try
            {
                await foreach (var (data, text) in client.Outbox.Reader.ReadAllAsync(gone.Token))
                    await socket.SendAsync(data, text ? WebSocketMessageType.Text : WebSocketMessageType.Binary, true, gone.Token);
            }
            catch { /* the browser went */ }
        });
        try
        {
            // The browser says how it's doing (its report, and the alive message sent back); reading is also how its leaving
            // is noticed. A message too big for this is skipped, not trusted.
            var buffer = new byte[4096];
            while (socket.State == WebSocketState.Open)
            {
                var used = 0;
                var tooBig = false;
                WebSocketReceiveResult r;
                do
                {
                    if (used == buffer.Length) { tooBig = true; used = 0; }
                    r = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, used, buffer.Length - used), CancellationToken.None);
                    used += r.Count;
                } while (!r.EndOfMessage && r.MessageType != WebSocketMessageType.Close);
                if (r.MessageType == WebSocketMessageType.Close) break;
                Volatile.Write(ref client.LastHeardAt, Environment.TickCount64);
                if (!tooBig && r.MessageType == WebSocketMessageType.Text) FromBrowser(client, buffer.AsSpan(0, used));
            }
        }
        catch { /* the connection dropped */ }
        finally
        {
            lock (clientsGate) clients.Remove(client);
            RecountCodecs();
            client.Outbox.Writer.TryComplete();
            gone.Cancel();
            try { await sending; } catch { }
            log($"web listener: browser {client.Number} left ({ListenerCount} now){(Describe(client) is { } last ? $": {last}" : "")}");
        }
    }

    /// <summary>What a browser sent: "pong", the alive message's time back, so the round trip is known; "report", the page's
    /// own figures. Only numbers are taken from it, so nothing a page says reaches the log as it was written.</summary>
    private void FromBrowser(Client client, ReadOnlySpan<byte> json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json.ToArray());
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            var type = root.TryGetProperty("type", out var tp) && tp.ValueKind == JsonValueKind.String ? tp.GetString() : null;
            if (type is "pong" or "report") client.AnswersBack = true;
            if (type == "pong" && root.TryGetProperty("t", out var sent) && sent.ValueKind == JsonValueKind.Number && sent.TryGetInt64(out var at))
            {
                var ms = Environment.TickCount64 - at;
                if (ms < 0 || ms > 120_000) return;
                lock (client.Gate)
                {
                    client.RoundTrips.Add((Environment.TickCount64, (int)ms));
                    client.RoundTrips.RemoveAll(x => Environment.TickCount64 - x.At > 60_000);
                }
            }
            else if (type == "report")
            {
                static int Num(JsonElement e, string name) =>
                    e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d) ? (int)Math.Clamp(Math.Round(d), -1, 1_000_000) : 0;
                var parts = new List<string>();
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
                    foreach (var s in streams.EnumerateArray().Take(4))
                        parts.Add($"stream {Num(s, "id")}: buffer {Num(s, "depthMs")} ms (aiming for {Num(s, "targetMs")}), longest wait {Num(s, "longestWaitMs")} ms, "
                                  + $"gaps {Num(s, "gaps")}, catch-ups {Num(s, "skips")}");
                var output = Num(root, "outMs");
                var rate = Num(root, "rate");
                lock (client.Gate)
                {
                    client.Report = (parts.Count > 0 ? string.Join("; ", parts) : "no sound playing")
                                    + (output > 0 ? $"; its sound card adds about {output} ms" : "")
                                    + (rate > 0 ? $" at {rate} Hz" : "");
                    client.Reconnects = Math.Max(0, Num(root, "reconnects"));
                }
            }
        }
        catch { /* not a message this end knows: ignored */ }
    }

    /// <summary>One browser in words: its page's figures, the round trip now and at worst in the last minute, what this end
    /// dropped, and how often it has had to reconnect. Null before it has said anything.</summary>
    private string? Describe(Client c)
    {
        lock (c.Gate)
        {
            if (c.Report is null && c.RoundTrips.Count == 0) return null;
            var trip = c.RoundTrips.Count > 0
                ? $"round trip {c.RoundTrips[^1].Ms} ms, worst {c.RoundTrips.Max(x => x.Ms)} ms in the last minute"
                : "round trip not yet known";
            return $"{(c.Pcm ? "16-bit PCM" : "Opus")}, {c.Report ?? "no report yet"}; {trip}; dropped here {Volatile.Read(ref c.Dropped)}"
                   + $"; reconnected {c.Reconnects} {(c.Reconnects == 1 ? "time" : "times")}";
        }
    }

    /// <summary>Each browser listening, in words, for the Web listener window's status (Ed, 2026-10-12).</summary>
    public IReadOnlyList<string> DescribeBrowsers()
    {
        Client[] all;
        lock (clientsGate) all = [.. clients];
        return [.. all.Select(c => $"Browser {c.Number}: {Describe(c) ?? "just joined"}.")];
    }

    /// <summary>Into the log when a browser's gaps, catch-ups, drops or reconnects have changed, and once a minute regardless,
    /// so how a connection held up can be read afterwards without anyone copying the page.</summary>
    private void LogBrowsers()
    {
        Client[] all;
        lock (clientsGate) all = [.. clients];
        foreach (var c in all)
        {
            var line = Describe(c);
            if (line is null) continue;
            var key = System.Text.RegularExpressions.Regex.Replace(line, @"buffer \d+ ms|round trip \d+ ms, worst \d+ ms|sound card adds about \d+ ms|aiming for \d+|longest wait \d+ ms", "");
            var now = Environment.TickCount64;
            if (key == c.LastLogged && now - c.LastLoggedAt < 60_000) continue;
            c.LastLogged = key;
            c.LastLoggedAt = now;
            log($"web listener: browser {c.Number}: {line}");
        }
    }

    /// <summary>A message with a type other than "auth": a report or a reply to an alive, never an answer to the challenge.</summary>
    private static bool IsOtherThanAnAnswer(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("type", out var t)
                   && t.ValueKind == JsonValueKind.String && t.GetString() != "auth";
        }
        catch { return false; }
    }

    /// <summary>Null if the answer is right; otherwise the reason given to the browser.</summary>
    private string? CheckAnswer(byte[] nonce, string? response, byte[] challengeSalt)
    {
        var (salt, key) = Credentials();
        if (key is null) return "No password for listening has been set in RemSound on the computer yet.";
        // The password changed while this browser was answering: its answer is for the old one.
        if (!ReferenceEquals(salt, challengeSalt)) return "The password was changed. Start again.";
        lock (failuresGate)
        {
            while (failures.Count > 0 && DateTime.UtcNow - failures.Peek() > LockoutWindow) failures.Dequeue();
            if (failures.Count >= FailuresBeforeLockout)
            {
                log("web listener: a browser was refused - too many wrong passwords recently");
                return "Too many wrong passwords. Try again in a few minutes.";
            }
        }
        var expected = HMACSHA256.HashData(key, nonce);
        byte[]? given = null;
        try { given = response is null ? null : Convert.FromBase64String(response); } catch { }
        if (given is not null && CryptographicOperations.FixedTimeEquals(expected, given)) return null;
        lock (failuresGate) failures.Enqueue(DateTime.UtcNow);
        log("web listener: a browser was refused - wrong password");
        Thread.Sleep(WrongPasswordDelayMs);   // a wrong guess costs a second
        return "The password was not accepted.";
    }

    private static async Task SendText(WebSocket socket, string text)
    {
        try { await socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None); }
        catch { /* gone */ }
    }

    private static async Task<string?> ReceiveText(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[4096];
        var used = 0;
        try
        {
            while (true)
            {
                var r = await socket.ReceiveAsync(buffer.AsMemory(used), ct);
                if (r.MessageType == WebSocketMessageType.Close) return null;
                used += r.Count;
                if (r.EndOfMessage) return r.MessageType == WebSocketMessageType.Text ? Encoding.UTF8.GetString(buffer, 0, used) : null;
                if (used == buffer.Length) return null;
            }
        }
        catch { return null; }
    }

    private static async Task CloseQuietly(WebSocket socket)
    {
        try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { }
    }

    public void Dispose()
    {
        if (!running) return;
        running = false;
        try { listener.Stop(); } catch { }
        signal.Set();
        List<Client> all;
        lock (clientsGate) { all = [.. clients]; clients.Clear(); }
        foreach (var c in all) { c.Outbox.Writer.TryComplete(); try { c.Socket.Abort(); } catch { } }
        RecountCodecs();
        worker.Join(500);
        acceptThread.Join(500);
        foreach (var lane in lanes.Values) (lane.Opus as IDisposable)?.Dispose();
        signal.Dispose();
        log("web listener: closed");
    }
}
