using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Concentus;
using RemSound.Core;
using RemSound.Sender;

namespace RemSound.App;

/// <summary>
/// LISTEN IN A WEB BROWSER (WebListener, 2026-10-10). A browser is played here by a WebSocket client doing exactly what
/// the page does: answer the challenge with HMAC-SHA256 keyed by PBKDF2-SHA256 of the password, then take numbered
/// frames per sending lane. The Opus frames are decoded and their tone measured, so "it sent something" is not enough.
/// </summary>
internal static partial class SelfTest
{
    private const string WebPassword = "kettle9tiger42moon";

    private sealed class WebClient : IDisposable
    {
        public readonly ClientWebSocket Socket = new();
        public readonly ConcurrentQueue<string> Texts = new();
        public readonly ConcurrentQueue<(byte Stream, uint Sequence, byte[] Payload, long At)> Frames = new();
        private readonly CancellationTokenSource stop = new();
        private Task? reading;

        public void Connect(int port) => Socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/listen"), CancellationToken.None).GetAwaiter().GetResult();

        public string? NextText(int timeoutMs = 3000)
        {
            var deadline = Environment.TickCount64 + timeoutMs;
            while (Environment.TickCount64 < deadline)
            {
                if (Texts.TryDequeue(out var t)) return t;
                Thread.Sleep(5);
            }
            return null;
        }

        public void StartReading()
        {
            reading = Task.Run(async () =>
            {
                var buffer = new byte[65536];
                try
                {
                    while (!stop.IsCancellationRequested)
                    {
                        var used = 0;
                        ValueWebSocketReceiveResult r;
                        do
                        {
                            r = await Socket.ReceiveAsync(buffer.AsMemory(used), stop.Token);
                            used += r.Count;
                        } while (!r.EndOfMessage);
                        if (r.MessageType == WebSocketMessageType.Close) return;
                        if (r.MessageType == WebSocketMessageType.Text) Texts.Enqueue(Encoding.UTF8.GetString(buffer, 0, used));
                        else if (used >= 6 && buffer[0] == 1)
                            Frames.Enqueue((buffer[1], BitConverter.ToUInt32(buffer, 2), buffer[6..used], Environment.TickCount64));
                    }
                }
                catch { /* closed */ }
            });
        }

        public void Send(string text) => Socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None).GetAwaiter().GetResult();

        public void Dispose()
        {
            stop.Cancel();
            try { reading?.Wait(500); } catch { }
            Socket.Dispose();
        }
    }

    /// <summary>What the page computes: HMAC-SHA256(PBKDF2-SHA256(password, salt, iterations), nonce).</summary>
    private static string WebAnswer(string challengeJson, string password)
    {
        using var doc = JsonDocument.Parse(challengeJson);
        var salt = Convert.FromBase64String(doc.RootElement.GetProperty("salt").GetString()!);
        var iterations = doc.RootElement.GetProperty("iterations").GetInt32();
        var nonce = Convert.FromBase64String(doc.RootElement.GetProperty("nonce").GetString()!);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);
        return Convert.ToBase64String(HMACSHA256.HashData(key, nonce));
    }

    /// <summary>Connect and log in as the page does; returns the client and the server's last word.</summary>
    private static (WebClient Client, string? Reply) WebLogin(int port, string password, string codec = "opus")
    {
        var c = new WebClient();
        c.Connect(port);
        c.StartReading();
        var challenge = c.NextText();
        if (challenge is null) return (c, null);
        c.Send(JsonSerializer.Serialize(new { type = "auth", response = WebAnswer(challenge, password), codec }));
        return (c, c.NextText());
    }

    private static float[] Tone(int frames, double hz, int startFrame, float amplitude = 0.5f)
    {
        var block = new float[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            var v = (float)(amplitude * Math.Sin(2 * Math.PI * hz * (startFrame + i) / 48000));
            block[2 * i] = v;
            block[2 * i + 1] = v;
        }
        return block;
    }

    /// <summary>The tone in decoded left-channel samples, by rising zero crossings.</summary>
    private static double ToneHz(IReadOnlyList<short> left)
    {
        var crossings = 0;
        for (var i = 1; i < left.Count; i++) if (left[i - 1] < 0 && left[i] >= 0) crossings++;
        return crossings * 48000.0 / Math.Max(1, left.Count);
    }

    private static string WebListenerStep()
    {
        var said = new List<string>();
        var log = new ConcurrentQueue<string>();
        var muted = false;
        var oldDelay = WebListener.WrongPasswordDelayMs;
        WebListener.WrongPasswordDelayMs = 0;
        try
        {
            using (var listener = new WebListener(0, WebPassword, 2000, () => muted, log.Enqueue))
            {
                var port = listener.Port;

                // --- The page itself -------------------------------------------------------------------------
                using (var http = new HttpClient())
                {
                    var page = http.GetAsync($"http://127.0.0.1:{port}/").GetAwaiter().GetResult();
                    var body = page.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    Check(page.IsSuccessStatusCode && body.Contains("<title>RemSound listener</title>", StringComparison.Ordinal),
                        $"THE PAGE: / must serve the listening page ({(int)page.StatusCode})");
                    Check(page.Headers.TryGetValues("Content-Security-Policy", out var csp) && csp.First().Contains("connect-src 'self'", StringComparison.Ordinal),
                        "THE PAGE: it must be served with a policy that lets it talk to nothing but this computer");
                    var missing = http.GetAsync($"http://127.0.0.1:{port}/settings.json").GetAwaiter().GetResult();
                    Check((int)missing.StatusCode == 404, $"THE PAGE: nothing else may be served ({(int)missing.StatusCode})");
                }
                said.Add("the page is served, under a policy that keeps it to this computer, and nothing else is");

                // --- A wrong password ------------------------------------------------------------------------
                var (wrong, wrongReply) = WebLogin(port, "not-the-password");
                Check(wrongReply is not null && wrongReply.Contains("\"refused\"", StringComparison.Ordinal) && wrongReply.Contains("not accepted", StringComparison.Ordinal),
                    $"A WRONG PASSWORD: it must be refused ({wrongReply})");
                wrong.Dispose();
                Check(listener.ListenerCount == 0, "A WRONG PASSWORD: it must not be counted as listening");
                said.Add("a wrong password is refused");

                // --- Starting by itself: said in the challenge, before any password, so a page can tell -------------
                using (var asking = new WebClient())
                {
                    listener.AutoStart = true;
                    asking.Connect(port);
                    asking.StartReading();
                    var challenge = asking.NextText() ?? "";
                    Check(challenge.Contains("\"autoStart\":true", StringComparison.Ordinal), $"START BY ITSELF: ticked, the challenge must say so ({challenge})");
                    listener.AutoStart = false;
                }
                using (var asking = new WebClient())
                {
                    asking.Connect(port);
                    asking.StartReading();
                    var challenge = asking.NextText() ?? "";
                    Check(challenge.Contains("\"autoStart\":false", StringComparison.Ordinal), $"START BY ITSELF: unticked, the challenge must say so, so a remembered password is forgotten ({challenge})");
                }
                said.Add("the challenge says whether the page starts by itself");

                // --- The right one, and two lanes at once ---------------------------------------------------
                var (browser, welcome) = WebLogin(port, WebPassword);
                Check(welcome is not null && welcome.Contains("\"welcome\"", StringComparison.Ordinal), $"THE PASSWORD: the right one must be let in ({welcome})");
                Check(welcome!.Contains($"\"source\":{JsonSerializer.Serialize(listener.SourceName)}", StringComparison.Ordinal) && listener.SourceName.Length > 0,
                    $"RECEIVING FROM: once in, the page must be told this computer's name ({welcome})");
                Check(WaitUntil(() => listener.ListenerCount == 1), "THE PASSWORD: it must count as one browser listening");

                // A real AudioSender's DAW lane (AsioLane in WASAPI-only) through the app's own tap, plus the WASAPI
                // capture lane's Mixed route fed in 10 ms blocks: two lanes, two clocks, two block sizes.
                using var sink = new WireSink();
                using var sender = new AudioSender();
                ArmSender(sender, sink);
                sender.OnWebSamples = listener.Feed;
                sender.SetPluginSendActive(true);
                var fedAt = new ConcurrentDictionary<int, long>();
                for (var i = 0; i < 40; i++)
                {
                    // 40 x 10 ms of 440 Hz on Mixed; 40 x 64-frame blocks of 880 Hz on the DAW lane.
                    if (i == 20) fedAt[0] = Environment.TickCount64;
                    listener.Feed(Tone(480, 440, i * 480), RenderRoute.Mixed);
                    sender.SubmitPluginBlock(Tone(64, 880, i * 64));
                    // At the pace a card delivers it. All at once, the listener's 160 ms per browser would rightly drop
                    // the oldest, which is how a browser that falls behind jumps back to live.
                    Thread.Sleep(10);
                }
                Check(WaitUntil(() => browser.Frames.Count(f => f.Stream == 0) >= 80 && browser.Frames.Count(f => f.Stream == (byte)RenderRoute.AsioLane) >= 10, 3000),
                    $"TWO LANES: both must reach the browser ({browser.Frames.Count(f => f.Stream == 0)} Mixed frames of 80, {browser.Frames.Count(f => f.Stream == 2)} DAW-lane frames of 10)");
                sender.OnWebSamples = null;

                var frames = browser.Frames.ToArray();
                foreach (var (stream, hz, expectFrames) in new[] { ((byte)0, 440.0, 80), ((byte)RenderRoute.AsioLane, 880.0, 10) })
                {
                    var mine = frames.Where(f => f.Stream == stream).ToArray();
                    Check(mine.Select((f, i) => f.Sequence == (uint)i).All(ok => ok),
                        $"STREAM {stream}: its frames must be numbered 0, 1, 2 ... with none missing ({string.Join(",", mine.Take(6).Select(f => f.Sequence))})");
                    var decoder = OpusCodecFactory.CreateDecoder(48000, 2, TextWriter.Null);
                    var left = new List<short>();
                    var pcm = new short[WebListener.FrameSamples * 2];
                    foreach (var f in mine)
                    {
                        var n = decoder.Decode(f.Payload, pcm, WebListener.FrameSamples, false);
                        Check(n == WebListener.FrameSamples, $"STREAM {stream}: every frame must decode to 5 ms (240 samples), got {n}");
                        for (var i = 0; i < n; i++) left.Add(pcm[2 * i]);
                    }
                    (decoder as IDisposable)?.Dispose();
                    // Past the codec's start-up, the tone must be the one fed in, at a real level.
                    var settled = left.Skip(left.Count / 4).ToList();
                    var measured = ToneHz(settled);
                    var rms = Math.Sqrt(settled.Average(s => (double)s * s)) / 32768;
                    Check(Math.Abs(measured - hz) < hz * 0.05, $"STREAM {stream}: the browser must hear {hz} Hz, heard {measured:0} Hz");
                    Check(rms > 0.25, $"STREAM {stream}: and at the level sent (rms {rms:0.000}, a 0.5 sine is 0.354)");
                    Check(mine.Length >= expectFrames, $"STREAM {stream}: every whole frame fed must arrive ({mine.Length} of {expectFrames})");
                }
                // How long a frame takes from the tap to the browser on this computer: the encoding and the WebSocket.
                var twentieth = frames.Where(f => f.Stream == 0).ElementAt(40);
                var tapToBrowserMs = twentieth.At - fedAt[0];
                Check(tapToBrowserMs < 100, $"SPEED: a frame must reach the browser well within 100 ms of the tap here (took {tapToBrowserMs} ms)");
                said.Add($"two lanes at once (10 ms WASAPI blocks and 64-frame DAW blocks through the real sender's tap) arrive as two streams of numbered 5 ms Opus frames, decoded at 440 and 880 Hz at full level, {tapToBrowserMs} ms from tap to browser here");

                // --- Muted: nothing, and the browser is told -----------------------------------------------
                listener.IdleAfterMs = 200;
                while (browser.Texts.TryDequeue(out _)) { }
                var before = browser.Frames.Count;
                muted = true;
                for (var i = 0; i < 10; i++) listener.Feed(Tone(480, 440, i * 480), RenderRoute.Mixed);
                Check(WaitUntil(() => browser.Texts.Any(t => t.Contains("\"idle\"", StringComparison.Ordinal)), 2000),
                    "MUTED: the browser must be told nothing is being sent");
                Check(browser.Frames.Count == before, $"MUTED: no sound may reach the browser ({browser.Frames.Count - before} frames did)");
                muted = false;
                listener.Feed(Tone(480, 440, 0), RenderRoute.Mixed);
                Check(WaitUntil(() => browser.Texts.Any(t => t.Contains("\"active\"", StringComparison.Ordinal)), 2000),
                    "UNMUTED: the browser must be told sound is back");
                said.Add("muted, no sound reaches the browser and it is told so; unmuted, it is told sound is back");

                // --- 16-bit PCM for a browser without an Opus decoder ----------------------------------------
                var (pcmBrowser, pcmWelcome) = WebLogin(port, WebPassword, "pcm16");
                Check(pcmWelcome is not null && pcmWelcome.Contains("\"welcome\"", StringComparison.Ordinal), $"PCM: a browser asking for PCM must be let in ({pcmWelcome})");
                Check(WaitUntil(() => listener.ListenerCount == 2), "PCM: two browsers must count as two");
                for (var i = 0; i < 4; i++) listener.Feed(Tone(480, 440, i * 480, 0.25f), RenderRoute.Mixed);
                Check(WaitUntil(() => pcmBrowser.Frames.Count >= 8), $"PCM: its frames must arrive ({pcmBrowser.Frames.Count})");
                var firstPcm = pcmBrowser.Frames.ToArray()[0].Payload;
                Check(firstPcm.Length == WebListener.FrameSamples * 4, $"PCM: a frame must be 240 stereo 16-bit samples ({firstPcm.Length} bytes)");
                var expected = Tone(WebListener.FrameSamples, 440, 0, 0.25f);
                var worst = 0.0;
                for (var i = 0; i < expected.Length; i++) worst = Math.Max(worst, Math.Abs(BitConverter.ToInt16(firstPcm, i * 2) / 32767.0 - expected[i]));
                Check(worst < 0.0005, $"PCM: the samples must be the ones sent (worst error {worst:0.000000})");
                pcmBrowser.Dispose();
                browser.Dispose();
                Check(WaitUntil(() => listener.ListenerCount == 0), $"LEAVING: closed browsers must stop counting ({listener.ListenerCount})");
                said.Add("a browser asking for 16-bit PCM gets the exact samples; browsers that leave stop counting");

                // --- A new password, in force at once ------------------------------------------------------------
                var (stays, _) = WebLogin(port, WebPassword);
                Check(WaitUntil(() => listener.ListenerCount == 1), "premise: a browser is listening before the password changes");
                listener.SetPassword("new-" + WebPassword);
                Check(WaitUntil(() => listener.ListenerCount == 0 && stays.Socket.State != WebSocketState.Open, 2000),
                    $"A NEW PASSWORD: the browser listening must be disconnected ({listener.ListenerCount} listening, socket {stays.Socket.State})");
                stays.Dispose();
                var (old, oldReply) = WebLogin(port, WebPassword);
                Check(oldReply is not null && oldReply.Contains("not accepted", StringComparison.Ordinal), $"A NEW PASSWORD: the old one must be refused ({oldReply})");
                old.Dispose();
                var (fresh, freshReply) = WebLogin(port, "new-" + WebPassword);
                Check(freshReply is not null && freshReply.Contains("\"welcome\"", StringComparison.Ordinal), $"A NEW PASSWORD: the new one must be let in ({freshReply})");
                fresh.Dispose();
                listener.SetPassword(WebPassword);
                said.Add("a new password disconnects whoever is listening, refuses the old one and lets the new one in");

                // --- The lockout -------------------------------------------------------------------------------
                for (var i = 0; i < 9; i++) { var (c, _) = WebLogin(port, "guess" + i); c.Dispose(); }
                var (locked, lockedReply) = WebLogin(port, WebPassword);
                Check(lockedReply is not null && lockedReply.Contains("Too many", StringComparison.Ordinal),
                    $"THE LOCKOUT: after ten wrong passwords even the right one must wait ({lockedReply})");
                locked.Dispose();
                said.Add("ten wrong passwords lock everyone out for a while, the right password included");
            }

            // --- Closed, and no password --------------------------------------------------------------------
            var closedPort = FreeTcpPort();
            Check(!TcpAccepts(closedPort), "premise: a free port refuses connections");
            using (var noPassword = new WebListener(0, null, 0, () => false, log.Enqueue))
            {
                Check(!noPassword.HasPassword, "NO PASSWORD: a listener with none must say so");
                var (c, reply) = WebLogin(noPassword.Port, WebPassword);
                Check(reply is not null && reply.Contains("No password", StringComparison.Ordinal), $"NO PASSWORD: every browser must be refused ({reply})");
                c.Dispose();
                closedPort = noPassword.Port;
            }
            Check(!TcpAccepts(closedPort), "CLOSED: once closed, nothing may answer on its port");
            said.Add("with no password set every browser is refused; closed, the port answers nothing");

            var lines = log.ToArray();
            foreach (var expect in new[] { "web listener: open on 127.0.0.1:", "a browser is listening (1 now, Opus)", "refused - wrong password", "refused - too many wrong passwords", "a browser left", "web listener: closed", "no password is set yet", "the password changed - 1 browser(s) disconnected" })
                Check(lines.Any(l => l.Contains(expect, StringComparison.Ordinal)), $"THE LOG: it must record \"{expect}\"");
            Check(!lines.Any(l => l.Contains(WebPassword, StringComparison.Ordinal)), "THE LOG: the password may never be written to it");
            said.Add("the log records opening, each browser, each refusal, a password change and closing, and never the password");

            // --- Through its window on the Connectivity tab, into the profile -------------------------------
            said.Add(WebListenThroughItsWindow());
        }
        finally
        {
            WebListener.WrongPasswordDelayMs = oldDelay;
        }
        return string.Join("; ", said);
    }

    private static string WebListenThroughItsWindow()
    {
        var said = new List<string>();
        var port = FreeTcpPort();
        MainForm.WebListenPortForTest = port;
        try
        {
            using var form = new MainForm(null, new Profile(), null, null, headless: true);
            var logged = new ConcurrentQueue<string>();
            UiChangeLog.Sink = (what, value) => logged.Enqueue($"{what}: {value}");
            Check(form.WebListenerForTest is null, "premise: a new profile has it off, so nothing is listening");

            // --- Cancel puts everything back ---------------------------------------------------------------------
            using (var dialog = form.BuildWebListenerDialog())
            {
                dialog.Show();
                var engine = new RemoteControlEngine(() => dialog);
                var reply = engine.Execute("set enableBox on");
                Check(reply.StartsWith("ok", StringComparison.Ordinal), $"THE TICK BOX: it must be reachable through the control channel ({reply})");
                Check(form.WebListenerForTest is { } open && open.Port == port && !open.HasPassword,
                    "THE TICK BOX: ticked, the listener must open at once on its port, with no password yet");
                dialog.RefreshStatusForTest();
                Check(dialog.StatusBoxForTest.Text.Contains("No password is set", StringComparison.Ordinal) && dialog.StatusBoxForTest.Text.Contains($"port {port}", StringComparison.Ordinal),
                    $"THE STATUS: it must say it's on, where, and that no password is set ({dialog.StatusBoxForTest.Text})");
                // Typed, then moved away from, as the control channel does it (validated, then left): the password is set.
                reply = engine.Execute("set passwordBox " + WebPassword);
                Check(reply.StartsWith("ok", StringComparison.Ordinal) && !reply.Contains(WebPassword, StringComparison.Ordinal),
                    $"THE PASSWORD BOX: it must take a password through the control channel, never saying it back ({reply})");
                Check(form.WebListenerForTest is { HasPassword: true }, "THE PASSWORD BOX: moved away from, the password must be in force at once");
                var (c, welcome) = WebLogin(port, WebPassword);
                Check(welcome is not null && welcome.Contains("\"welcome\"", StringComparison.Ordinal), $"THE PASSWORD BOX: a browser with it must be let in ({welcome})");
                Check(WaitUntil(() => form.WebListenerForTest?.ListenerCount == 1), "premise: the browser is listening");
                dialog.RefreshStatusForTest();
                Check(dialog.StatusBoxForTest.Text.Contains("1 browser is listening", StringComparison.Ordinal) && dialog.StatusBoxForTest.Text.Contains("A password is set", StringComparison.Ordinal),
                    $"THE STATUS: it must count the browser listening ({dialog.StatusBoxForTest.Text})");
                c.Dispose();
                reply = engine.Execute("set autoStartBox on");
                Check(reply.StartsWith("ok", StringComparison.Ordinal) && form.SettingsForTest.LoadWebListenAutoStart() && form.WebListenerForTest is { AutoStart: true },
                    $"START BY ITSELF: ticked through the control channel, it must be in force at once ({reply})");
                reply = engine.Execute("click cancelButton");
                Check(reply.StartsWith("ok", StringComparison.Ordinal), $"CANCEL: it must be pressable ({reply})");
                Check(form.WebListenerForTest is null && !form.SettingsForTest.LoadWebListenEnabled() && form.SettingsForTest.LoadWebListenPassword() == ""
                      && !form.SettingsForTest.LoadWebListenAutoStart(),
                    "CANCEL: the ticks and the password must go back to how they were: off, none, not starting by itself, nothing listening");
                form.AfterWebListenerDialog(dialog);
                Check(!form.UnsavedChangesForTest, "CANCEL: the profile must not be marked unsaved");
            }

            // --- OK keeps them, in the profile ---------------------------------------------------------------------
            using (var dialog = form.BuildWebListenerDialog())
            {
                dialog.Show();
                var engine = new RemoteControlEngine(() => dialog);
                engine.Execute("set enableBox on");
                engine.Execute("set autoStartBox on");
                // Typed but still in the box when OK is pressed: it counts.
                dialog.PasswordBoxForTest.Text = "  " + WebPassword + " ";
                var reply = engine.Execute("click okButton");
                Check(reply.StartsWith("ok", StringComparison.Ordinal) && dialog.DialogResult == DialogResult.OK, $"OK: it must be pressable and close the window ({reply})");
                form.AfterWebListenerDialog(dialog);
                Check(dialog.Changed && form.UnsavedChangesForTest, "OK: a change must mark the profile unsaved, to be kept with Save");
                Check(form.WebListenerForTest is { HasPassword: true }, "OK: the listener must stay open with the password");
                Check(form.SettingsForTest.LoadWebListenPassword() == WebPassword, "OK: the password must be what was in the box, trimmed like every RemSound password");
            }
            var saved = form.CaptureProfileForTest("Web listener test");
            Check(saved.WebListenEnabled && RemSoundCrypto.Deobfuscate(saved.WebListenPassword) == WebPassword && saved.WebListenAutoStart,
                "THE PROFILE: saved, it must carry the ticks and the password");
            Check(!JsonSerializer.Serialize(saved).Contains(WebPassword, StringComparison.Ordinal), "THE PROFILE: the password kept scrambled like the profile password, never as plain text");
            using (var again = form.BuildWebListenerDialog())
                Check(again.EnableBoxForTest.Checked && again.PasswordBoxForTest.Text == WebPassword, "OPENED AGAIN: it must show the tick and the password");
            Check(logged.Any(l => l == "web listener: on, with a password, the page starts by itself"), $"LOGGED: OK with a change must be recorded ({string.Join(" | ", logged)})");
            Check(!logged.Any(l => l.Contains(WebPassword, StringComparison.Ordinal)), "LOGGED: never the password itself");

            // --- The address, Copy address and Turn on Tailscale Funnel, with a pretend Tailscale ------------------------
            said.Add(WebListenerFunnelParts(form));

            // --- Each profile its own: a profile that has it off closes it, one that has it on opens it ---------------
            form.ApplyThenCaptureForTest(new Profile());
            form.ApplyWebListenSetting();
            Check(form.WebListenerForTest is null, "ANOTHER PROFILE: one with it off must close the listener");
            form.ApplyThenCaptureForTest(saved);
            form.ApplyWebListenSetting();
            Check(form.WebListenerForTest is { HasPassword: true }, "BACK AGAIN: the profile with it on must open it, with its password");
            var (back, backWelcome) = WebLogin(port, WebPassword);
            Check(backWelcome is not null && backWelcome.Contains("\"welcome\"", StringComparison.Ordinal), $"BACK AGAIN: its password must let a browser in ({backWelcome})");
            back.Dispose();
            return string.Join("; ", said) + "; the Web listener window: ticked it opens at once; what's typed is the password once you move away, in force at once; the status counts browsers; Cancel puts it all back; OK keeps it and marks the profile unsaved; saved in the profile scrambled, shown again, logged without the password; each profile its own";
        }
        finally
        {
            MainForm.WebListenPortForTest = 0;
        }
    }

    /// <summary>Pump the window's own messages until <paramref name="task"/> ends: its awaits come back on this thread.</summary>
    private static void PumpUntil(Task task, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!task.IsCompleted && Environment.TickCount64 < deadline) { Application.DoEvents(); Thread.Sleep(5); }
    }

    private static string WebListenerFunnelParts(MainForm form)
    {
        const string Address = "https://gate-pc.tail-gate.ts.net";
        var funnelOn = false;
        var opened = new List<string>();
        var turnOns = 0;
        var hooks = new WebListenerDialog.FunnelHooks(
            _ => Task.FromResult(new TailscaleFunnel.State(true, Address, funnelOn, funnelOn ? "Tailscale Funnel is on." : "Tailscale Funnel is off for RemSound's page.")),
            (openPage, _) =>
            {
                turnOns++;
                openPage("https://login.tailscale.com/f/funnel?node=gate");   // the first time, Tailscale asks for approval
                funnelOn = true;
                return Task.FromResult((true, "Tailscale Funnel is on."));
            },
            opened.Add);
        using (var dialog = form.BuildWebListenerDialog(hooks))
        {
            dialog.Show();
            PumpUntil(dialog.AskTailscaleForTest());
            Check(dialog.AddressBoxForTest.Text == Address + " (once Tailscale Funnel is on)",
                $"THE ADDRESS: with Funnel off it must show where the page will be, and say so ({dialog.AddressBoxForTest.Text})");
            dialog.RefreshStatusForTest();
            Check(dialog.StatusBoxForTest.Text.Contains("Tailscale Funnel is off", StringComparison.Ordinal), $"THE STATUS: it must say Funnel is off ({dialog.StatusBoxForTest.Text})");
            var engine = new RemoteControlEngine(() => dialog);
            var reply = engine.Execute("click funnelButton");
            Check(reply.StartsWith("ok", StringComparison.Ordinal), $"TURN ON FUNNEL: the button must be pressable through the control channel ({reply})");
            for (var deadline = Environment.TickCount64 + 5000; dialog.AddressBoxForTest.Text != Address && Environment.TickCount64 < deadline; Thread.Sleep(5))
                Application.DoEvents();
            Check(turnOns == 1 && opened.SequenceEqual(["https://login.tailscale.com/f/funnel?node=gate"]),
                $"TURN ON FUNNEL: it must run Tailscale once and open the approval page it gives ({turnOns} runs; opened: {string.Join(", ", opened)})");
            Check(dialog.AddressBoxForTest.Text == Address, $"TURN ON FUNNEL: once on, the address must be the plain address, ready to copy ({dialog.AddressBoxForTest.Text})");
            reply = engine.Execute("click copyButton");
            Check(reply.StartsWith("ok", StringComparison.Ordinal), $"COPY ADDRESS: the button must be pressable ({reply})");
            var copied = "";
            try { copied = Clipboard.GetText(); } catch { /* checked below */ }
            Check(copied == Address, $"COPY ADDRESS: the clipboard must hold the address ({copied})");
            dialog.Close();
        }
        // Without Tailscale the button goes to its download page instead.
        opened.Clear();
        var missing = new WebListenerDialog.FunnelHooks(
            _ => Task.FromResult(new TailscaleFunnel.State(false, null, false, "Tailscale isn't installed on this computer.")),
            (_, _) => throw new InvalidOperationException("Tailscale must not be run when it isn't installed"), opened.Add);
        using (var dialog = form.BuildWebListenerDialog(missing))
        {
            dialog.Show();
            PumpUntil(dialog.AskTailscaleForTest());
            Check(dialog.AddressBoxForTest.Text == "None yet.", $"NO TAILSCALE: there is no address ({dialog.AddressBoxForTest.Text})");
            PumpUntil(dialog.TurnOnFunnelForTest());
            Check(opened.SequenceEqual([TailscaleFunnel.DownloadPage]), $"NO TAILSCALE: the button must open its download page ({string.Join(", ", opened)})");
            dialog.Close();
        }
        // Tailscale's own answer, read as the window reads it.
        const string status = """{"TCP":{"443":{"HTTPS":true}},"Web":{"ed-pc.tail1.ts.net:443":{"Handlers":{"/":{"Proxy":"http://127.0.0.1:47840"}}}},"AllowFunnel":{"ed-pc.tail1.ts.net:443":true}}""";
        Check(TailscaleFunnel.TryFindFunnel(status, 47840, out var on, out var host) && on && host == "ed-pc.tail1.ts.net",
            "FUNNEL'S ANSWER: Funnel to this port must be read as on, at the computer's name");
        Check(TailscaleFunnel.TryFindFunnel(status, 47841, out _, out _) == false, "FUNNEL'S ANSWER: Funnel to another port is not RemSound's");
        Check(TailscaleFunnel.TryFindFunnel(status.Replace("\"AllowFunnel\":{\"ed-pc.tail1.ts.net:443\":true}", "\"AllowFunnel\":{}"), 47840, out var off, out _) && !off,
            "FUNNEL'S ANSWER: served but not let in from the internet is off");
        return "the address shows where the page will be; Turn on Tailscale Funnel runs Tailscale once, opens its approval page and then shows the address; Copy address puts it on the clipboard; without Tailscale the button opens its download page; Funnel's own answer is read right";
    }

    private static int FreeTcpPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static bool TcpAccepts(int port)
    {
        try
        {
            using var c = new TcpClient();
            return c.ConnectAsync(IPAddress.Loopback, port).Wait(500) && c.Connected;
        }
        catch { return false; }
    }
}
