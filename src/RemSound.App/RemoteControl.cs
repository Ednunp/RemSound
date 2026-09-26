using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace RemSound.App;

/// <summary>
/// Driving RemSound with no window: the control channel of a <c>--headless</c> copy (see <see cref="Windowless"/>).
///
/// <para>Ed, 2026-09-24: "a way of driving remsound entirely without a window like I think you can with reaper."
/// The command line could already START RemSound a particular way, but nothing could change anything while it ran,
/// and reading the window through accessibility needs the window on screen — which is exactly what popped up on him
/// and read out while he was listening for something else.</para>
///
/// <para><b>Everything, by the names a screen reader reads.</b> The commands do not know about RemSound's features.
/// They walk whatever windows are open and act on their controls the way a person does — a tick box is clicked, a
/// list item ticked, a menu item chosen, a dialog's button pressed — down the same event handlers a mouse or a key
/// would reach. So every control, menu and dialog there is is reachable, and anything added later is reachable the
/// day it is added, with nothing here to update. A control is named by what NVDA reads for it (its accessible name,
/// its text, or the label in front of it), and also by the field that holds it in code, for when two share a name.</para>
///
/// <para><b>Who can use it.</b> Only a copy started with <c>--headless</c> listens at all. The channel is a named pipe
/// that only the Windows account running RemSound may open, refused outright to anything arriving over the network,
/// and the client checks the other end is that same account before it sends a word. Passwords are never read back.
/// Every command is written to the log with what it did.</para>
/// </summary>
internal static class RemoteControl
{
    /// <summary>One pipe per Windows session, so two people signed in to one PC never meet.</summary>
    internal static string PipeName(int sessionId) => $"RemSound.control.{sessionId}";

    internal static string DefaultPipeName => PipeName(Process.GetCurrentProcess().SessionId);

    /// <summary>How long a command may take before the reply says it is waiting on something it opened.</summary>
    internal static readonly TimeSpan CommandWait = TimeSpan.FromSeconds(3);

    /// <summary><c>RemSound --control "command"</c> (as many as given, in order): send each to the headless copy on
    /// this account and print its answer. 0 when every command was carried out, 1 when one was refused, 2 when no
    /// headless copy answered.</summary>
    internal static int RunClient(IReadOnlyList<string> commands, string? pipeName = null)
    {
        if (commands.Count == 0)
        {
            Console.WriteLine("RemSound --control needs a command after it, for example: RemSound --control \"help\"");
            return 1;
        }
        var rc = 0;
        foreach (var command in commands)
        {
            var reply = Send(command, pipeName ?? DefaultPipeName, out var reached);
            Console.WriteLine(reply);
            if (!reached) return 2;
            if (reply.StartsWith("error", StringComparison.OrdinalIgnoreCase)) rc = 1;
        }
        return rc;
    }

    /// <summary>Send one command and return the answer. <paramref name="reached"/> is false when nothing was listening.</summary>
    internal static string Send(string command, string pipeName, out bool reached, int connectTimeoutMs = 10000)
    {
        reached = false;
        // Nothing listening is the common case when somebody runs --control by mistake: say so at once, not after the
        // connect timeout. The pipe is re-made between commands, so give it a moment before deciding it isn't there.
        if (!PipeExists(pipeName, TimeSpan.FromSeconds(1))) return NobodyListening();
        try
        {
            // CurrentUserOnly: refuse to talk to a pipe some other account created under our name.
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            pipe.Connect(connectTimeoutMs);
            reached = true;
            var bytes = Encoding.UTF8.GetBytes(command.Replace('\r', ' ').Replace('\n', ' ') + "\n");
            pipe.Write(bytes, 0, bytes.Length);
            pipe.Flush();
            using var reader = new StreamReader(pipe, Encoding.UTF8);
            return reader.ReadToEnd().TrimEnd();
        }
        catch (TimeoutException)
        {
            return NobodyListening();
        }
        catch (Exception ex) when (!reached)
        {
            return NobodyListening() + $" ({ex.GetType().Name}: {ex.Message})";
        }
    }

    private static bool PipeExists(string pipeName, TimeSpan wait)
    {
        var deadline = DateTime.UtcNow + wait;
        while (true)
        {
            try
            {
                // The pipe namespace is \\.\pipe\. One backslash short this was C:\pipe\, which threw, which read as "there is
                // a pipe": with nothing listening, every --control waited ten seconds instead of saying so at once (2026-09-25).
                if (Directory.EnumerateFiles(@"\\.\pipe\").Any(p => string.Equals(Path.GetFileName(p), pipeName, StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
            catch { return true; /* can't tell: let the connect decide */ }
            if (DateTime.UtcNow >= deadline) return false;
            Thread.Sleep(50);
        }
    }

    private static string NobodyListening()
    {
        var me = Process.GetCurrentProcess();
        var others = Process.GetProcessesByName("RemSound").Where(p => p.Id != me.Id && p.SessionId == me.SessionId).ToList();
        return others.Count > 0
            ? "RemSound is running, but it wasn't started with --headless, so it has no control channel. Close it (RemSound --close) and start it again with --headless."
            : "No RemSound is running with --headless on this account. Start one with: RemSound --headless";
    }
}

/// <summary>The listening end: one command per connection, carried out on the UI thread, answered, closed.</summary>
internal sealed class RemoteControlServer : IDisposable
{
    private readonly string pipeName;
    private readonly Func<Control?> marshal;
    private readonly RemoteControlEngine engine;
    private readonly CancellationTokenSource stop = new();
    private Thread? thread;

    /// <summary>Where each command and its outcome is written (the app's log).</summary>
    public Action<string>? Log { get; set; }

    /// <param name="marshal">A control on the UI thread with a live window handle, asked for afresh every command:
    /// commands run there, through it. Afresh, because a profile switch closes the main window and opens a new one,
    /// and WinForms destroys its hidden helper windows as the old one's message loop ends — a control fixed at start-up
    /// went dead the first time a profile was opened through the channel (2026-09-24).</param>
    public RemoteControlServer(string pipeName, Func<Control?> marshal, RemoteControlEngine engine)
    {
        this.pipeName = pipeName;
        this.marshal = marshal;
        this.engine = engine;
    }

    public string PipeName => pipeName;

    public void Start()
    {
        thread = new Thread(Loop) { IsBackground = true, Name = "RemSound remote control" };
        thread.Start();
    }

    private NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();
        var me = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("no user SID");
        security.AddAccessRule(new PipeAccessRule(me, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
        // Nothing from the network, even this account signed in from elsewhere: this is a local control.
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        return NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
    }

    private void Loop()
    {
        var failures = 0;
        while (!stop.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try { pipe = CreatePipe(); failures = 0; }
            catch (Exception ex)
            {
                // Another process already holds the name, most likely. Say so once, and keep trying: it may go.
                if (failures++ == 0) Log?.Invoke($"remote control: can't open the control channel {pipeName}: {ex.GetType().Name}: {ex.Message}");
                if (stop.Token.WaitHandle.WaitOne(2000)) return;
                continue;
            }
            using (pipe)
            {
                try { pipe.WaitForConnectionAsync(stop.Token).GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { Log?.Invoke($"remote control: a connection failed: {ex.GetType().Name}: {ex.Message}"); continue; }
                try
                {
                    var line = ReadLine(pipe);
                    var reply = Run(line);
                    var bytes = Encoding.UTF8.GetBytes(reply + "\n");
                    pipe.Write(bytes, 0, bytes.Length);
                    pipe.Flush();
                    pipe.WaitForPipeDrain();
                }
                catch (Exception ex) { Log?.Invoke($"remote control: answering failed: {ex.GetType().Name}: {ex.Message}"); }
                try { pipe.Disconnect(); } catch { /* it's going anyway */ }
            }
        }
    }

    private static string ReadLine(Stream stream)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (bytes.Count < 64 * 1024 && stream.Read(one, 0, 1) == 1 && one[0] != (byte)'\n') bytes.Add(one[0]);
        return Encoding.UTF8.GetString(bytes.ToArray()).Trim();
    }

    /// <summary>Carry out one command on the UI thread and return the answer. A command that opens something modal
    /// (a dialog, a question) does not come back until that closes, so after <see cref="RemoteControl.CommandWait"/>
    /// the answer says what it opened and the command goes on waiting inside it. The UI thread still takes the next
    /// command, which is how that dialog then gets answered.</summary>
    internal string Run(string line)
    {
        if (line.Length == 0) return "error: no command. Say help for the list.";
        // wait runs HERE, on the channel's own thread, and looks at the window only in short turns: waiting on the window's
        // thread would stop the very thing being waited for from happening.
        if (line.Equals("wait", StringComparison.OrdinalIgnoreCase) || line.StartsWith("wait ", StringComparison.OrdinalIgnoreCase))
        {
            var waited = Wait(line[4..].Trim());
            Log?.Invoke($"remote control: {Redact(line)} -> {waited.Split('\n')[0]}");
            return waited;
        }
        var reply = OnUiThread(() => engine.Execute(line), RemoteControl.CommandWait, out var finished);
        // Read now, before anything else runs a command: the "windows" below resets it. And a command that has not finished
        // cannot say what it reached, so any value it carries is hidden - a password box addressed by its id reads nothing
        // like "password", and was logged in the clear when its command took longer than the wait (2026-09-25).
        var secret = finished ? engine.LastTouchedSecret : IsValueCommand(line);
        if (!finished)
        {
            var open = OnUiThread(() => engine.Execute("windows"), TimeSpan.FromSeconds(2), out var listed);
            reply = "ok - that opened something that is waiting for an answer.\n" + (listed ? open : RemoteControlEngine.NativeDialogLines());
        }
        Log?.Invoke($"remote control: {(secret ? HideValue(line) : Redact(line))} -> {reply.Split('\n')[0]}");
        return reply;
    }

    /// <summary>wait [gone] &lt;text&gt; [seconds]: until a window or question whose listing has the text is open (or, with
    /// gone, is not), or a log line with it is written after the wait begins. Ten seconds unless told, two minutes at most.</summary>
    private string Wait(string rest)
    {
        var tokens = RemoteControlEngine.Tokens(rest);
        var gone = tokens.Count > 0 && tokens[0].Equals("gone", StringComparison.OrdinalIgnoreCase);
        if (gone) tokens.RemoveAt(0);
        var seconds = 10.0;
        if (tokens.Count > 1 && double.TryParse(tokens[^1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s))
        {
            seconds = Math.Clamp(s, 0.1, 120);
            tokens.RemoveAt(tokens.Count - 1);
        }
        var text = string.Join(' ', tokens);
        if (text.Length == 0) return "error: wait for what? For example: wait Preferences, wait gone Preferences, or wait \"receiver listener\" 20";
        var mark = HeadlessRecords.Log.Mark;
        var started = DateTime.UtcNow;
        while (true)
        {
            var windows = OnUiThread(() => engine.Execute("windows"), TimeSpan.FromSeconds(2), out _);
            var inWindows = windows.Contains(text, StringComparison.OrdinalIgnoreCase);
            var inLog = !gone && HeadlessRecords.Log.Since(mark).Any(l => l.Contains(text, StringComparison.OrdinalIgnoreCase));
            var took = (DateTime.UtcNow - started).TotalSeconds;
            if (gone ? !inWindows : inWindows || inLog)
                return $"ok - {Quote(text)} {(gone ? "is gone" : inWindows ? "is open" : "was logged")} after {took:0.0} s";
            if (took >= seconds)
                return $"error: {Quote(text)} {(gone ? "is still there" : "did not appear")} within {seconds:0.#} s";
            Thread.Sleep(100);
        }
    }

    private static string Quote(string s) => "\"" + s + "\"";

    /// <summary>The command with its value - its last word, or all of a quoted last value - hidden. Only the last word of a
    /// quoted value went, once (2026-09-25).</summary>
    internal static string HideValue(string line) => Regex.Replace(line, "\\s(\"[^\"]*\"|\\S+)\\s*$", " (hidden)");

    /// <summary>A command that puts a value somewhere: set, key or type.</summary>
    internal static bool IsValueCommand(string line) => Regex.IsMatch(line, "^(set|key|keys|type)\\s", RegexOptions.IgnoreCase);

    private string OnUiThread(Func<string> work, TimeSpan wait, out bool finished)
    {
        string? result = null;
        var done = new ManualResetEventSlim();
        try
        {
            var target = marshal();
            if (target is null || target.IsDisposed || !target.IsHandleCreated)
            {
                finished = true;
                return "error: RemSound is between windows (a profile is opening). Try again in a moment.";
            }
            target.BeginInvoke(() =>
            {
                try { result = work(); }
                catch (Exception ex) { result = $"error: {ex.GetType().Name}: {ex.Message}"; }
                finally { done.Set(); }
            });
        }
        catch (Exception ex)
        {
            finished = true;
            return $"error: RemSound isn't taking commands right now ({ex.GetType().Name}: {ex.Message})";
        }
        finished = done.Wait(wait);
        return finished ? result ?? "" : "";
    }

    /// <summary>A password typed through the channel must not land in the log in the clear.</summary>
    internal static string Redact(string line) =>
        Regex.IsMatch(line, "pass(word|phrase|key)", RegexOptions.IgnoreCase) && line.StartsWith("set ", StringComparison.OrdinalIgnoreCase)
            ? HideValue(line)
            : line;

    /// <summary>For the gate: run one command the way the pipe does, and hand back the line that would be logged.</summary>
    internal string LoggedLineForTest(string line)
    {
        string? logged = null;
        var previous = Log;
        Log = l => logged = l;
        try { Run(line); }
        finally { Log = previous; }
        return logged ?? "";
    }

    public void Dispose()
    {
        stop.Cancel();
        try { thread?.Join(2000); } catch { /* background thread, the process is going */ }
    }
}

/// <summary>
/// The commands. Everything here runs on the UI thread and works on whatever windows the app has open.
/// </summary>
internal sealed class RemoteControlEngine
{
    private readonly Func<Form?> mainForm;

    /// <param name="mainForm">The live main window, or null while there is none (the profile picker is up).</param>
    public RemoteControlEngine(Func<Form?> mainForm) => this.mainForm = mainForm;

    internal const string HelpText =
        "RemSound remote control. A control is named by what a screen reader reads for it, or by its id from list.\n" +
        "Put a name in double quotes when it runs into the next word ambiguously.\n" +
        "  windows                     the windows open now, the current one marked *, and any question waiting\n" +
        "  list [window]               every control in a window with its state (the current window by default)\n" +
        "  taborder [window]           the controls in the order Tab moves through them, on the tab page showing\n" +
        "  focus [window]              the control that has the keyboard\n" +
        "  texts [window]              the window's plain text: status lines, labels, messages\n" +
        "  get <control>               one control in full; a list's items with their ticks\n" +
        "  set <control> <value>       tick box on/off, text, number, slider, or a choice's item\n" +
        "  check <list> <item> [on|off]   tick or untick an item in a list (on if left out)\n" +
        "  select <list> <item>        move a list's cursor to an item\n" +
        "  click <control>             press a button, tick box or option\n" +
        "  menu <menu> > <item> [> <item>]   choose a menu item, for example: menu File > Save profile\n" +
        "  menus                       every menu item\n" +
        "  tab <tab>                   switch the main window's tab\n" +
        "  key <control> <keys>        send keys to a control, for example: key \"Connected peers\" space. A button's Alt key\n" +
        "                              presses it; a tick box's or a field's can't, as a hidden window can't take the keyboard\n" +
        "  answer <button>             answer the question or dialog that is waiting, for example: answer No\n" +
        "  f1 [control]                what F1 does: context help for a control (the one with the keyboard unless named);\n" +
        "                              get Help reads it, answer Close closes it\n" +
        "  manual                      what Shift+F1 does: the whole manual (a headless copy opens no browser)\n" +
        "  type <text>                 type into a Windows file or folder picker's name box, then answer Open\n" +
        "  status                      the connection status and health lines\n" +
        "  sounds on|off               let RemSound's sounds play, for testing one (off unless asked)\n" +
        "  wait [gone] <text> [secs]   until a window or question with that text is open (or gone), or a log line has it\n" +
        "  log [n] [words]             the newest log lines (20 unless told), even with the log file off\n" +
        "  cues [n] [words]            the cues RemSound would have played, newest last\n" +
        "  speech [n] [words]          what RemSound would have said to a screen reader, newest last\n" +
        "  settings                    this computer's settings and the profile as the window holds it, passwords hidden\n" +
        "  show                        open the window for the person at the keyboard (hands it over)\n" +
        "  quit                        close RemSound the way File, Exit does";

    /// <summary>The last command put something secret into a control (a password), so its words must not be logged.
    /// Decided by the control the command reached, not by how the command was worded: a password box addressed by its
    /// field id reads nothing like "password".</summary>
    internal bool LastTouchedSecret { get; private set; }

    public string Execute(string line)
    {
        LastTouchedSecret = false;
        var (verb, rest) = SplitVerb(line);
        return verb switch
        {
            "help" or "?" => HelpText,
            "windows" => Windows(),
            "list" => List(rest, interactive: true),
            "taborder" or "tab-order" => TabOrder(rest),
            "focus" => Focus(rest),
            "texts" or "text" => List(rest, interactive: false),
            "get" => Get(rest),
            "set" => Set(rest),
            "check" or "tick" => CheckItem(rest, null),
            "uncheck" or "untick" => CheckItem(rest, false),
            "select" => Select(rest),
            "click" or "press" => Click(rest),
            "menu" => Menu(rest),
            "menus" => Menus(),
            "tab" => Tab(rest),
            "key" or "keys" => Key(rest),
            "answer" => Answer(rest),
            "f1" => F1(rest),
            "manual" or "shift+f1" => Manual(),
            "type" => TypeIntoNativeDialog(rest),
            "status" => Status(),
            "sounds" or "sound" => Sounds(rest),
            "log" => Recent(HeadlessRecords.Log, rest, "logged"),
            "cues" => Recent(HeadlessRecords.Cues, rest, "no cue has fired"),
            "speech" => Recent(HeadlessRecords.Speech, rest, "nothing has been said"),
            "settings" => Settings(),
            "show" => Show(),
            "quit" or "exit" => Quit(),
            _ => $"error: I don't know \"{verb}\". Say help for the list.",
        };
    }

    /// <summary>log, cues, speech [n] [words]: the newest n (20 unless told) of what this copy has logged, which cues it
    /// would have played, or what it would have said, optionally only lines with the words in them.</summary>
    private static string Recent(RecentLines lines, string rest, string emptyMeans)
    {
        var tokens = Tokens(rest);
        var count = 20;
        if (tokens.Count > 0 && int.TryParse(tokens[0], out var n)) { count = Math.Clamp(n, 1, 500); tokens.RemoveAt(0); }
        var filter = string.Join(' ', tokens);
        var found = lines.Last(count, filter);
        if (found.Count > 0) return string.Join("\n", found);
        return filter.Length > 0 ? $"nothing with {Quote(filter)} in it" : emptyMeans == "logged" ? "nothing logged yet" : emptyMeans;
    }

    /// <summary>settings: this computer's RemSound settings and the profile as the window holds it now, as they would be
    /// saved - with every password, key and fingerprint hidden.</summary>
    private string Settings()
    {
        var machine = System.Text.Json.JsonSerializer.SerializeToNode(RemSound.Core.AppConfig.Load());
        var sb = new StringBuilder("This computer's settings:\n").Append(HideSecrets(machine)?.ToJsonString(IndentedJson));
        if (mainForm() is MainForm main)
            sb.Append("\n\nThe profile, as the window holds it now:\n")
              .Append(HideSecrets(System.Text.Json.JsonSerializer.SerializeToNode(main.CurrentProfileForControl()))?.ToJsonString(IndentedJson));
        return sb.ToString();
    }

    private static readonly System.Text.Json.JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    /// <summary>Every value under a name that sounds secret becomes "(hidden)" when it holds anything, "(none)" when not.</summary>
    internal static System.Text.Json.Nodes.JsonNode? HideSecrets(System.Text.Json.Nodes.JsonNode? node)
    {
        switch (node)
        {
            case System.Text.Json.Nodes.JsonObject obj:
                foreach (var (name, value) in obj.ToList())
                {
                    if (Regex.IsMatch(name, "password|secret|fingerprint|token|privatekey|signingkey|cryptokey", RegexOptions.IgnoreCase) && value is System.Text.Json.Nodes.JsonValue)
                        obj[name] = string.IsNullOrEmpty(value.ToString()) ? "(none)" : "(hidden)";
                    else HideSecrets(value);
                }
                break;
            case System.Text.Json.Nodes.JsonArray array:
                foreach (var item in array) HideSecrets(item);
                break;
        }
        return node;
    }

    // ---------------- windows ----------------

    /// <summary>The forms a person could be looking at: the main window and any dialog, not the invisible 1×1
    /// owners ForegroundDialog makes to bring a dialog forward.</summary>
    private List<Form> OpenForms()
    {
        var forms = Application.OpenForms.Cast<Form>().Where(f => !f.IsDisposed && (f.Controls.Count > 0 || f.MainMenuStrip is not null)).ToList();
        if (mainForm() is { IsDisposed: false } main && !forms.Contains(main)) forms.Insert(0, main);
        return forms;
    }

    /// <summary>The window a command acts on: the newest dialog that is up, or the main window.</summary>
    private Form? CurrentForm()
    {
        var forms = OpenForms();
        var main = mainForm();
        var dialog = forms.LastOrDefault(f => f != main && (f.Modal || f.Visible));
        return dialog ?? main ?? forms.LastOrDefault();
    }

    private string Windows()
    {
        var sb = new StringBuilder();
        var natives = NativeDialogLines();
        var current = natives.Length > 0 ? null : CurrentForm();
        var main = mainForm();
        foreach (var f in OpenForms())
        {
            if (f is HeadlessQuestionForm q)
            {
                // A message box, asked silently: say it the way a message box would be read.
                sb.Append(f == current ? "* " : "  ").Append("question: ").Append(Quote(q.Text)).Append(" says ").Append(Quote(q.Question.Replace("\r", "").Replace("\n", " ")))
                  .Append(" - answers: ").AppendLine(string.Join(", ", q.Answers.Select(Quote)));
                continue;
            }
            var kind = f == main ? "main window" : f.Modal ? "dialog" : "window";
            var where = Windowless.Hiding ? "hidden" : f.Visible ? "on screen" : "in the tray";
            sb.Append(f == current ? "* " : "  ").Append(kind).Append(": ").Append(Quote(f.Text)).Append(" (").Append(where).AppendLine(")");
        }
        if (natives.Length > 0) sb.Append(natives);
        return sb.Length == 0 ? "no windows are open" : sb.ToString().TrimEnd();
    }

    /// <summary>The app's message boxes that are waiting, with their text and buttons. Safe from any thread.</summary>
    internal static string NativeDialogLines()
    {
        var sb = new StringBuilder();
        foreach (var d in Windowless.NativeDialogs())
        {
            sb.Append("* question: ").Append(Quote(d.Title)).Append(" says ").Append(Quote(Windowless.Clean(d.Text)));
            sb.Append(" - answers: ").AppendLine(string.Join(", ", d.Buttons.Select(b => Quote(b.Text))));
        }
        return sb.ToString();
    }

    private Form? FormNamed(string name)
    {
        if (name.Length == 0) return CurrentForm();
        var forms = OpenForms();
        var key = Norm(name);
        return forms.FirstOrDefault(f => Norm(f.Text) == key)
            ?? (key is "main" or "main window" ? mainForm() : null)
            ?? forms.FirstOrDefault(f => Norm(f.Text).Contains(key, StringComparison.Ordinal));
    }

    // ---------------- listing ----------------

    private string List(string rest, bool interactive)
    {
        var form = FormNamed(rest.Trim().Trim('"'));
        if (form is null) return $"error: no open window is called {Quote(rest.Trim())}. Say windows to see them.";
        var ids = FieldNames(form);
        var sb = new StringBuilder().Append("window ").AppendLine(Quote(form.Text));
        foreach (var c in Walk(form))
        {
            if (interactive != IsInteractive(c)) continue;
            if (!interactive)
            {
                var text = c is Label or GroupBox ? c.Text : "";
                if (string.IsNullOrWhiteSpace(text)) continue;
                sb.Append("  ").AppendLine(Windowless.Clean(text).Replace("\r", "").Replace("\n", " / "));
                continue;
            }
            sb.Append("  ").AppendLine(Describe(c, ids));
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>The window's controls in the order Tab moves through them, on whichever tab page is showing - what a
    /// keyboard user actually meets, which <c>list</c> (the order the controls were added) is not. Built 2026-09-24 so
    /// tab-order changes can be checked with no window; the walk is GetNextControl, as the tab-order memory says to
    /// verify with, kept to real stops: selectable, a tab stop, and showing.</summary>
    private string TabOrder(string rest)
    {
        var form = FormNamed(rest.Trim().Trim('"'));
        if (form is null) return $"error: no open window is called {Quote(rest.Trim())}. Say windows to see them.";
        var ids = FieldNames(form);
        var sb = new StringBuilder().Append("tab order of ").AppendLine(Quote(form.Text));
        var n = 0;
        foreach (var c in TabStops(form)) sb.Append("  ").Append(++n).Append(". ").AppendLine(Describe(c, ids));
        if (n == 0) sb.AppendLine("  (nothing can take focus)");
        return sb.ToString().TrimEnd();
    }

    /// <summary>The controls Tab lands on, in order. The edit box WinForms builds inside a number box is reported as the
    /// number box, which is what a screen reader announces.</summary>
    internal static List<Control> TabStops(Form form)
    {
        var stops = new List<Control>();
        var seen = new HashSet<Control>();
        for (var c = form.GetNextControl(form, true); c is not null && seen.Add(c); c = form.GetNextControl(c, true))
        {
            // CanSelect asks Visible and Enabled all the way up, so a control on a tab page that is not showing is out.
            if (!c.TabStop || !c.CanSelect) continue;
            var stop = c.Parent is UpDownBase updown ? updown : c;
            if (!stops.Contains(stop)) stops.Add(stop);
        }
        return stops;
    }

    /// <summary>The control that has the keyboard in a window, however deep it sits.</summary>
    private string Focus(string rest)
    {
        var form = FormNamed(rest.Trim().Trim('"'));
        if (form is null) return $"error: no open window is called {Quote(rest.Trim())}. Say windows to see them.";
        Control? at = form.ActiveControl;
        while (at is ContainerControl { ActiveControl: { } inner } && inner != at) at = inner;
        if (at?.Parent is UpDownBase updown) at = updown;
        return at is null ? $"nothing in {Quote(form.Text)} has focus" : "focus: " + Describe(at, FieldNames(form));
    }

    private string Get(string rest)
    {
        var (c, error) = Resolve(rest.Trim(), acting: false);
        if (c is null) return error!;
        var sb = new StringBuilder().AppendLine(Describe(c, FieldNames(c.FindForm())));
        switch (c)
        {
            case CheckedListBox clb:
                for (var i = 0; i < clb.Items.Count; i++)
                    sb.Append("  ").Append(i + 1).Append(". ").Append(Quote(clb.GetItemText(clb.Items[i])))
                      .Append(clb.GetItemChecked(i) ? " ticked" : " not ticked").AppendLine(clb.SelectedIndex == i ? ", selected" : "");
                break;
            case ListBox lb:
                for (var i = 0; i < lb.Items.Count; i++)
                    sb.Append("  ").Append(i + 1).Append(". ").Append(Quote(lb.GetItemText(lb.Items[i]))).AppendLine(lb.GetSelected(i) ? " selected" : "");
                break;
            case ComboBox cb:
                for (var i = 0; i < cb.Items.Count; i++)
                    sb.Append("  ").Append(i + 1).Append(". ").Append(Quote(cb.GetItemText(cb.Items[i]))).AppendLine(cb.SelectedIndex == i ? " chosen" : "");
                break;
            case TabControl tc:
                foreach (TabPage p in tc.TabPages)
                    sb.Append("  ").Append(Quote(Windowless.Clean(p.Text))).AppendLine(tc.SelectedTab == p ? " showing" : "");
                break;
            case TextBoxBase tb when !IsPassword(tb):
                sb.AppendLine(tb.Text);
                break;
        }
        return sb.ToString().TrimEnd();
    }

    private string Status()
    {
        var main = mainForm();
        if (main is null) return "error: the main window isn't open yet";
        var sb = new StringBuilder();
        foreach (var c in Walk(main))
        {
            if (c is TextBoxBase tb && Norm(NameOf(c)).StartsWith("connection status", StringComparison.Ordinal)) sb.AppendLine(tb.Text.Replace("\r", ""));
            if (c is Label l && l.Text.StartsWith("Health:", StringComparison.Ordinal)) sb.AppendLine(l.Text);
        }
        foreach (var c in Walk(main))
            if (c is Label l && l.Text.StartsWith("Connected for", StringComparison.Ordinal)) sb.AppendLine(l.Text);
        return sb.Length == 0 ? "error: no status lines found" : sb.ToString().TrimEnd();
    }

    // ---------------- acting ----------------

    private string Set(string rest)
    {
        var tokens = Tokens(rest);
        if (tokens.Count < 2) return "error: set needs a control and a value, for example: set \"Send my audio\" on";
        var value = tokens[^1];
        var (c, error) = Resolve(string.Join(' ', tokens.Take(tokens.Count - 1)));
        if (c is null) return error!;
        if (!c.Enabled) return $"error: {Quote(NameOf(c))} is unavailable right now";
        switch (c)
        {
            case CheckBox cb:
            {
                if (!TryOnOff(value, out var on)) return $"error: a tick box takes on or off, not {Quote(value)}";
                if (cb.Checked != on) UserClick(cb);
                return cb.Checked == on ? $"ok - {Quote(NameOf(c))} is {(on ? "ticked" : "not ticked")}" : $"error: {Quote(NameOf(c))} didn't change (it may have been refused)";
            }
            case RadioButton rb:
            {
                if (!TryOnOff(value, out var on) || !on) return "error: an option can only be chosen (on); choose another to move off it";
                if (!rb.Checked) UserClick(rb);
                return $"ok - {Quote(NameOf(c))} is chosen";
            }
            case NumericUpDown nud:
            {
                if (!decimal.TryParse(value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var n)) return $"error: {Quote(value)} isn't a number";
                nud.Value = Math.Clamp(n, nud.Minimum, nud.Maximum);
                return $"ok - {Quote(NameOf(c))} is {nud.Value}";
            }
            case TrackBar tb:
            {
                if (!int.TryParse(value, out var n)) return $"error: {Quote(value)} isn't a whole number";
                tb.Value = Math.Clamp(n, tb.Minimum, tb.Maximum);
                CallProtected(tb, "OnScroll", EventArgs.Empty);
                return $"ok - {Quote(NameOf(c))} is {tb.Value}";
            }
            case ComboBox combo:
            {
                var i = FindItem(Enumerable.Range(0, combo.Items.Count).Select(k => combo.GetItemText(combo.Items[k]) ?? "").ToList(), value);
                if (i < 0)
                {
                    if (combo.DropDownStyle == ComboBoxStyle.DropDown) { combo.Text = value; FinishEdit(combo); return $"ok - {Quote(NameOf(c))} is {Quote(combo.Text)}"; }
                    return $"error: {Quote(NameOf(c))} has no choice {Quote(value)}. Say get {Quote(NameOf(c))} to see them.";
                }
                combo.SelectedIndex = i;
                CallProtected(combo, "OnSelectionChangeCommitted", EventArgs.Empty);
                return $"ok - {Quote(NameOf(c))} is {Quote(combo.GetItemText(combo.SelectedItem))}";
            }
            case TextBoxBase text:
            {
                if (IsPassword(text)) LastTouchedSecret = true;
                text.Text = value;
                FinishEdit(text);
                return $"ok - {Quote(NameOf(c))} is set{(IsPassword(text) ? "" : " to " + Quote(text.Text))}";
            }
            case ListBox:
                return Select(rest);
            default:
                return $"error: {Quote(NameOf(c))} is a {KindOf(c)}, which set doesn't change. Try click.";
        }
    }

    private string CheckItem(string rest, bool? force)
    {
        var tokens = Tokens(rest);
        var on = force ?? true;
        if (force is null && tokens.Count > 0 && TryOnOff(tokens[^1], out var said)) { on = said; tokens.RemoveAt(tokens.Count - 1); }
        if (tokens.Count < 2) return "error: check needs a list and an item, for example: check \"Connected peers\" iPhone";
        var (list, item, error) = ResolveListAndItem(tokens);
        if (list is null) return error!;
        if (list is not CheckedListBox clb) return $"error: {Quote(NameOf(list))} has no tick boxes. Try select.";
        if (!clb.Enabled) return $"error: {Quote(NameOf(list))} is unavailable right now";
        clb.SelectedIndex = item;
        if (clb.GetItemChecked(item) != on) clb.SetItemChecked(item, on);
        var text = clb.GetItemText(clb.Items[item]);
        return clb.GetItemChecked(item) == on
            ? $"ok - {Quote(text)} is {(on ? "ticked" : "not ticked")} in {Quote(NameOf(list))}"
            : $"error: {Quote(text)} didn't change (it may have been refused)";
    }

    private string Select(string rest)
    {
        var (list, item, error) = ResolveListAndItem(Tokens(rest));
        if (list is null) return error!;
        switch (list)
        {
            case ListBox lb:
                if (lb.SelectionMode is SelectionMode.MultiSimple or SelectionMode.MultiExtended) lb.ClearSelected();
                lb.SelectedIndex = item;
                return $"ok - the cursor is on {Quote(lb.GetItemText(lb.Items[item]))} in {Quote(NameOf(list))}";
            case ComboBox cb:
                cb.SelectedIndex = item;
                CallProtected(cb, "OnSelectionChangeCommitted", EventArgs.Empty);
                return $"ok - {Quote(NameOf(list))} is {Quote(cb.GetItemText(cb.SelectedItem))}";
            default:
                return $"error: {Quote(NameOf(list))} isn't a list";
        }
    }

    private string Click(string rest)
    {
        var (c, error) = Resolve(rest.Trim());
        if (c is null) return error!;
        if (!c.Enabled) return $"error: {Quote(NameOf(c))} is unavailable right now";
        var name = NameOf(c);
        UserClick(c);
        return c switch
        {
            CheckBox cb => $"ok - {Quote(name)} is {(cb.Checked ? "ticked" : "not ticked")}",
            RadioButton => $"ok - {Quote(name)} is chosen",
            _ => $"ok - pressed {Quote(name)}",
        };
    }

    private string Tab(string rest)
    {
        var want = Norm(rest.Trim().Trim('"'));
        var blocked = Blocker() is not null;
        foreach (var form in new[] { CurrentForm(), blocked ? null : mainForm() }.Where(f => f is not null).Distinct())
            foreach (var tc in Walk(form!).OfType<TabControl>())
                foreach (TabPage p in tc.TabPages)
                    if (Norm(Windowless.Clean(p.Text)) == want || Norm(Windowless.Clean(p.Text)).StartsWith(want, StringComparison.Ordinal))
                    {
                        tc.SelectedTab = p;
                        return $"ok - the {Quote(Windowless.Clean(p.Text))} tab is showing";
                    }
        return $"error: no tab called {Quote(rest.Trim())}";
    }

    private string Answer(string rest)
    {
        var want = Norm(rest.Trim().Trim('"'));
        var natives = Windowless.NativeDialogs();
        if (natives.Count > 0)
        {
            var d = natives[^1];
            var button = d.Buttons.FirstOrDefault(b => Norm(b.Text) == want);
            if (button.Text is null) return $"error: the question {Quote(d.Text)} has no answer {Quote(rest.Trim())}. It offers {string.Join(", ", d.Buttons.Select(b => Quote(b.Text)))}.";
            Windowless.PressNativeButton(d, button.Id);
            return $"ok - answered {Quote(button.Text)} to {Quote(d.Text)}";
        }
        var form = CurrentForm();
        if (form is null || form == mainForm()) return "error: nothing is waiting for an answer";
        var target = Walk(form).OfType<ButtonBase>().FirstOrDefault(b => Norm(NameOf(b)) == want)
                     ?? Walk(form).OfType<ButtonBase>().FirstOrDefault(b => Norm(NameOf(b)).StartsWith(want, StringComparison.Ordinal));
        if (target is null)
            return $"error: {Quote(form.Text)} has no button {Quote(rest.Trim())}. Its buttons: "
                   + string.Join(", ", Walk(form).OfType<Button>().Where(b => b.Enabled && OwnVisible(b)).Select(b => Quote(NameOf(b))));
        UserClick(target);
        return $"ok - pressed {Quote(NameOf(target))} in {Quote(form.Text)}";
    }

    /// <summary>f1 [control]: exactly what pressing F1 does - that control's help (2026-09-25). With no control named, the
    /// one that has the keyboard in the current window. The help window opens after this returns, as it does after the
    /// real key.</summary>
    private string F1(string rest)
    {
        Control? c;
        if (rest.Trim().Length == 0)
        {
            var form = CurrentForm();
            if (form is null) return "error: no window is open";
            c = form.ActiveControl;
            while (c is ContainerControl { ActiveControl: { } inner } && inner != c) c = inner;
            if (c is null) return $"error: nothing in {Quote(form.Text)} has the keyboard - name a control, for example: f1 \"Buffer smoothness\"";
        }
        else
        {
            var (found, error) = Resolve(rest.Trim(), acting: false);
            if (found is null) return error!;
            c = found;
        }
        var target = c;
        if (ContextHelp.Find(target) is not (_, var key))
        {
            HelpLauncher.OpenManual();
            return $"{Quote(NameOf(target))} has no context help, so F1 opens the whole manual there"
                 + (Windowless.Active ? " (a headless copy opens no browser; the log says where it would have gone)" : "");
        }
        target.BeginInvoke(() => { if (!target.IsDisposed) ContextHelp.Show(target); });
        return $"ok - F1 on {Quote(NameOf(target))}: its context help ({key}) is opening. get Help reads it, answer Close closes it";
    }

    /// <summary>manual: what Shift+F1 does - the whole manual in the browser.</summary>
    private static string Manual()
    {
        HelpLauncher.OpenManual();
        return Windowless.Active
            ? "ok - Shift+F1: the whole manual. A headless copy opens no browser; the log says where it would have gone"
            : "ok - Shift+F1: the whole manual is opening in the browser";
    }

    /// <summary>Windows' own file and folder pickers are not WinForms at all: their name box is filled in directly.</summary>
    private static string TypeIntoNativeDialog(string rest)
    {
        var natives = Windowless.NativeDialogs();
        if (natives.Count == 0) return "error: type is for a Windows file or folder picker, and none is open. For RemSound's own boxes, use set.";
        var text = rest.Trim();
        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"') text = text[1..^1];
        return Windowless.TypeIntoNameBox(natives[^1], text)
            ? $"ok - typed {Quote(text)} into {Quote(natives[^1].Title)}"
            : $"error: {Quote(natives[^1].Title)} has no box to type into";
    }

    private string Menu(string rest)
    {
        var path = rest.Split('>', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(p => p.Trim('"')).ToList();
        if (path.Count == 0) return "error: menu needs a path, for example: menu File > Save profile";
        if (Blocker() is { } waiting) return $"error: {waiting} is waiting for an answer, and nothing else can be used until it has one. Say windows.";
        var form = CurrentForm() is { MainMenuStrip: not null } f ? f : mainForm();
        var strip = form?.MainMenuStrip ?? (form is null ? null : Walk(form).OfType<MenuStrip>().FirstOrDefault());
        if (strip is null) return "error: this window has no menu";
        ToolStripItemCollection items = strip.Items;
        ToolStripMenuItem? item = null;
        foreach (var part in path)
        {
            item = FindMenuItem(items, part);
            if (item is null)
                return $"error: no menu item {Quote(part)}. Here: {string.Join(", ", items.OfType<ToolStripMenuItem>().Where(i => i.Available).Select(i => Quote(MenuText(i))))}";
            if (part != path[^1]) { Open(item); items = item.DropDownItems; }
        }
        if (!item!.Enabled) return $"error: {Quote(MenuText(item))} is unavailable right now";
        if (item.HasDropDownItems)
        {
            Open(item);
            return $"{Quote(MenuText(item))} is a menu: {string.Join(", ", item.DropDownItems.OfType<ToolStripMenuItem>().Where(i => i.Available).Select(i => Quote(MenuText(i))))}";
        }
        item.PerformClick();
        return $"ok - chose {string.Join(" > ", path.Select(Quote))}";
    }

    private string Menus()
    {
        var form = CurrentForm() is { MainMenuStrip: not null } f ? f : mainForm();
        var strip = form?.MainMenuStrip ?? (form is null ? null : Walk(form).OfType<MenuStrip>().FirstOrDefault());
        if (strip is null) return "error: this window has no menu";
        var sb = new StringBuilder();
        void Tree(ToolStripItemCollection items, int depth)
        {
            foreach (var i in items.OfType<ToolStripMenuItem>().Where(i => i.Available))
            {
                sb.Append(new string(' ', depth * 2)).Append(MenuText(i));
                if (i.Checked) sb.Append(" (ticked)");
                if (!i.Enabled) sb.Append(" (unavailable)");
                sb.AppendLine();
                Open(i);
                if (i.HasDropDownItems) Tree(i.DropDownItems, depth + 1);
            }
        }
        Tree(strip.Items, 0);
        return sb.ToString().TrimEnd();
    }

    private string Key(string rest)
    {
        var tokens = Tokens(rest);
        if (tokens.Count < 2) return "error: key needs a control and keys, for example: key \"Connected peers\" space";
        var (c, error) = Resolve(string.Join(' ', tokens.Take(tokens.Count - 1)));
        if (c is null) return error!;
        if (c is TextBoxBase typedInto && IsPassword(typedInto)) LastTouchedSecret = true;
        if (!TryParseKeys(tokens[^1], out var keys)) return $"error: I can't read the keys {Quote(tokens[^1])}";
        SendKeysTo(c, keys);
        return $"ok - sent {tokens[^1]} to {Quote(NameOf(c))}";
    }

    /// <summary>A headless copy is silent (Ed, 2026-09-24: "if you're testing a sound then fair enough but otherwise
    /// it should be silent and invisible"). This lets the sounds play for as long as a sound is being tested.</summary>
    private static string Sounds(string rest)
    {
        switch (rest.Trim().ToLowerInvariant())
        {
            case "on":
                CuePlayer.GloballyMuted = false;
                return "ok - RemSound's sounds will play. Say sounds off when the test is done.";
            case "off":
                CuePlayer.GloballyMuted = true;
                return "ok - RemSound is silent again";
            case "":
                return CuePlayer.GloballyMuted ? "RemSound is silent" : "RemSound's sounds are playing";
            default:
                return "error: sounds takes on or off";
        }
    }

    private string Show()
    {
        if (mainForm() is not MainForm main) return "error: the main window isn't open yet";
        main.RestoreFromTray();
        return "ok - the window is open for the person at the keyboard; windows behave normally from now on";
    }

    private string Quit()
    {
        if (mainForm() is not MainForm main) { Application.Exit(); return "ok - closing"; }
        main.BeginInvoke(new Action(main.CloseFromCommandLine));
        return "ok - closing the way File, Exit does";
    }

    // ---------------- finding controls ----------------

    /// <summary>Find a control by name in the current window, then in any open window: an exact name or id first,
    /// then a name that starts with it, then one that contains it. Two that fit equally well is an error that lists
    /// them with their ids, never a guess.</summary>
    private (Control? Control, string? Error) Resolve(string name, bool acting = true)
    {
        name = name.Trim().Trim('"');
        if (name.Length == 0) return (null, "error: which control? Say list to see them.");
        // A question box stops everything behind it, for a person and so here: answer it first.
        if (acting && Windowless.NativeDialogs() is { Count: > 0 } natives)
            return (null, $"error: the question {Quote(natives[^1].Text)} is waiting for an answer, and nothing else can be used until it has one. Say answer and one of: {string.Join(", ", natives[^1].Buttons.Select(b => Quote(b.Text)))}");
        var current = CurrentForm();
        var forms = new List<Form>();
        if (current is not null) forms.Add(current);
        // While a dialog is waiting, it is the only window a person can use: Windows disables the rest. Reaching past it
        // would run the main window's code inside the dialog, a state nobody at the keyboard can ever create.
        if (Blocker() is null) forms.AddRange(OpenForms().Where(f => f != current));
        foreach (var form in forms)
        {
            var found = Candidates(form, name);
            if (found.Count == 1) return (found[0], null);
            if (found.Count > 1)
            {
                var ids = FieldNames(form);
                return (null, $"error: {Quote(name)} could be any of these - use its id:\n" + string.Join("\n", found.Select(c => "  " + Describe(c, ids))));
            }
        }
        var why = Blocker() is { } waiting ? $" While {waiting} is waiting, only it can be used." : "";
        return (null, $"error: no control called {Quote(name)} in {Quote(current?.Text ?? "any window")}.{why} Say list to see them.");
    }

    /// <summary>The dialog or question a person would have to deal with before anything else, if there is one.</summary>
    private string? Blocker()
    {
        if (Windowless.NativeDialogs() is { Count: > 0 } natives) return $"the question {Quote(natives[^1].Text)}";
        return CurrentForm() is { Modal: true } dialog && dialog != mainForm() ? Quote(dialog.Text) : null;
    }

    private static List<Control> Candidates(Form form, string name)
    {
        var key = Norm(name);
        var ids = FieldNames(form);
        var all = Walk(form).Where(IsInteractive).ToList();
        var exact = all.Where(c => Norm(NameOf(c)) == key || (ids.TryGetValue(c, out var id) && string.Equals(id, name, StringComparison.OrdinalIgnoreCase))).ToList();
        if (exact.Count > 0) return exact;
        var starts = all.Where(c => Norm(NameOf(c)).StartsWith(key, StringComparison.Ordinal)).ToList();
        if (starts.Count > 0) return starts;
        return all.Where(c => Norm(NameOf(c)).Contains(key, StringComparison.Ordinal)).ToList();
    }

    /// <summary>A list and an item in it, from words like <c>Connected peers iPhone</c>: the list is the longest run of
    /// leading words that names a list, the item the rest (its text, or its number from get).</summary>
    private (Control? List, int Item, string? Error) ResolveListAndItem(List<string> tokens)
    {
        for (var split = tokens.Count - 1; split >= 1; split--)
        {
            var listName = string.Join(' ', tokens.Take(split));
            var (c, _) = Resolve(listName);
            if (c is not (ListBox or ComboBox)) continue;
            var itemName = string.Join(' ', tokens.Skip(split));
            var texts = c is ListBox lb
                ? Enumerable.Range(0, lb.Items.Count).Select(i => lb.GetItemText(lb.Items[i]) ?? "").ToList()
                : Enumerable.Range(0, ((ComboBox)c).Items.Count).Select(i => ((ComboBox)c).GetItemText(((ComboBox)c).Items[i]) ?? "").ToList();
            var index = FindItem(texts, itemName);
            if (index >= 0) return (c, index, null);
            return (null, -1, $"error: {Quote(NameOf(c))} has no item {Quote(itemName)}. It has: {(texts.Count == 0 ? "nothing" : string.Join(", ", texts.Select(Quote)))}");
        }
        return (null, -1, $"error: no list called {Quote(string.Join(' ', tokens))}. Say list to see them.");
    }

    /// <summary>An item by its number (1-based, as get shows), its exact text, or the one item whose text contains it.</summary>
    private static int FindItem(IReadOnlyList<string> texts, string want)
    {
        want = want.Trim().Trim('"');
        if (int.TryParse(want, out var n) && n >= 1 && n <= texts.Count && !texts.Any(t => Norm(t) == Norm(want))) return n - 1;
        var key = Norm(want);
        for (var i = 0; i < texts.Count; i++) if (Norm(texts[i]) == key) return i;
        var starts = Enumerable.Range(0, texts.Count).Where(i => Norm(texts[i]).StartsWith(key, StringComparison.Ordinal)).ToList();
        if (starts.Count == 1) return starts[0];
        var contains = Enumerable.Range(0, texts.Count).Where(i => Norm(texts[i]).Contains(key, StringComparison.Ordinal)).ToList();
        return contains.Count == 1 ? contains[0] : -1;
    }

    private static ToolStripMenuItem? FindMenuItem(ToolStripItemCollection items, string name)
    {
        var key = Norm(name);
        var all = items.OfType<ToolStripMenuItem>().Where(i => i.Available).ToList();
        return all.FirstOrDefault(i => Norm(MenuText(i)) == key || Norm(i.AccessibleName ?? "") == key || Norm(i.AccessibleName ?? "") == key + " menu")
            ?? all.FirstOrDefault(i => Norm(MenuText(i)).StartsWith(key, StringComparison.Ordinal));
    }

    private static string MenuText(ToolStripItem i) => CleanLabel(i.Text ?? "");

    /// <summary>Menus that fill themselves in as they open (recent profiles, say) need to be told they are opening.</summary>
    private static void Open(ToolStripMenuItem item)
    {
        // DropDownOpening is raised by OnDropDownShow; some menus fill in on DropDownOpened instead.
        try { CallProtected(item, "OnDropDownShow", EventArgs.Empty); } catch { /* nothing to fill in */ }
        try { CallProtected(item, "OnDropDownOpened", EventArgs.Empty); } catch { /* nothing to fill in */ }
    }

    // ---------------- describing ----------------

    /// <summary>What a screen reader reads for a control: its accessible name, else its own text, else the label in
    /// front of it, else its id. "&amp;" marks and the "(Alt+X)" hints are dropped.</summary>
    internal static string NameOf(Control c)
    {
        if (!string.IsNullOrWhiteSpace(c.AccessibleName)) return CleanLabel(c.AccessibleName);
        if (c is ButtonBase or GroupBox or Label or TabPage && !string.IsNullOrWhiteSpace(c.Text)) return CleanLabel(c.Text);
        if (PrecedingLabel(c) is { } label) return CleanLabel(label.Text);
        return c.Name ?? "";
    }

    private static Label? PrecedingLabel(Control c)
    {
        if (c.Parent is null) return null;
        var siblings = c.Parent.Controls.Cast<Control>().ToList();
        var byTab = siblings.OfType<Label>().Where(l => l.TabIndex < c.TabIndex && !string.IsNullOrWhiteSpace(l.Text)).OrderByDescending(l => l.TabIndex).FirstOrDefault();
        if (byTab is not null && byTab.TabIndex == c.TabIndex - 1) return byTab;
        var at = siblings.IndexOf(c);
        for (var i = at - 1; i >= 0; i--)
        {
            if (siblings[i] is Label l && !string.IsNullOrWhiteSpace(l.Text)) return l;
            if (IsInteractive(siblings[i])) break;
        }
        // The label may sit one level up, beside the panel that holds the control (the peer lists are built so).
        return c.Parent is { Parent: not null } && c.Parent.Controls.Count <= 3 && c.Parent is not TabPage and not Form ? PrecedingLabel(c.Parent) : byTab;
    }

    private static string CleanLabel(string text)
    {
        var s = Windowless.Clean(text).Replace("\r", " ").Replace("\n", " ");
        s = Regex.Replace(s, @"\s*\(Alt\+\S+\)\s*$", "");
        return s.TrimEnd(':', ' ');
    }

    private static string Norm(string s) => Regex.Replace(CleanLabel(s).ToLowerInvariant(), @"\s+", " ").Trim().TrimEnd('.', '…');

    private static string Describe(Control c, IReadOnlyDictionary<Control, string> ids)
    {
        var sb = new StringBuilder().Append(KindOf(c)).Append(' ').Append(Quote(NameOf(c)));
        switch (c)
        {
            case CheckBox cb: sb.Append(cb.Checked ? " ticked" : " not ticked"); break;
            case RadioButton rb: sb.Append(rb.Checked ? " chosen" : " not chosen"); break;
            case TextBoxBase tb: sb.Append(IsPassword(tb) ? " (a password, not shown)" : " = " + Quote(Short(tb.Text))); if (tb.ReadOnly) sb.Append(" read only"); break;
            case NumericUpDown nud: sb.Append(" = ").Append(nud.Value).Append($" ({nud.Minimum} to {nud.Maximum})"); break;
            case TrackBar tb: sb.Append(" = ").Append(tb.Value).Append($" ({tb.Minimum} to {tb.Maximum})"); break;
            case ComboBox cb: sb.Append(" = ").Append(Quote(cb.GetItemText(cb.SelectedItem) is { Length: > 0 } t ? t : cb.Text)).Append($" ({cb.Items.Count} choices)"); break;
            case CheckedListBox clb: sb.Append($" {clb.Items.Count} items, {clb.CheckedIndices.Count} ticked"); if (clb.SelectedIndex >= 0) sb.Append(", cursor on ").Append(Quote(clb.GetItemText(clb.SelectedItem))); break;
            case ListBox lb: sb.Append($" {lb.Items.Count} items"); if (lb.SelectedIndex >= 0) sb.Append(", cursor on ").Append(Quote(lb.GetItemText(lb.SelectedItem))); break;
            case TabControl tc: sb.Append(" showing ").Append(Quote(Windowless.Clean(tc.SelectedTab?.Text ?? ""))); break;
        }
        if (!c.Enabled) sb.Append(" (unavailable)");
        if (!OwnVisible(c)) sb.Append(" (hidden)");
        if (TabOf(c) is { } tab) sb.Append(" [tab ").Append(Quote(tab)).Append(']');
        if (ids.TryGetValue(c, out var id)) sb.Append(" id=").Append(id);
        return sb.ToString();
    }

    private static string KindOf(Control c) => c switch
    {
        CheckBox => "tick box",
        RadioButton => "option",
        ButtonBase => "button",
        CheckedListBox => "tick list",
        ListBox => "list",
        ComboBox => "choice",
        TextBoxBase => "text box",
        NumericUpDown => "number",
        TrackBar => "slider",
        TabControl => "tabs",
        LinkLabel => "link",
        _ => c.GetType().Name,
    };

    private static bool IsInteractive(Control c) =>
        c is ButtonBase or ListControl or TextBoxBase or NumericUpDown or TrackBar or TabControl or LinkLabel or DateTimePicker;

    private static bool IsPassword(TextBoxBase t) => KeyClickService.IsPasswordField(t);

    private static string? TabOf(Control c)
    {
        for (var p = c.Parent; p is not null; p = p.Parent)
            if (p is TabPage page) return Windowless.Clean(page.Text);
        return null;
    }

    private static IEnumerable<Control> Walk(Control root)
    {
        foreach (Control c in root.Controls)
        {
            yield return c;
            if (c is UpDownBase or ComboBox) continue;
            foreach (var inner in Walk(c)) yield return inner;
        }
    }

    /// <summary>The field that holds each control in the form's code: a second name, unique, for when two controls
    /// read the same.</summary>
    private static Dictionary<Control, string> FieldNames(Form? form)
    {
        var map = new Dictionary<Control, string>();
        if (form is null) return map;
        for (var t = form.GetType(); t is not null && t != typeof(Form); t = t.BaseType)
            foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly))
                if (typeof(Control).IsAssignableFrom(f.FieldType) && f.GetValue(form) is Control c && !map.ContainsKey(c))
                    map[c] = f.Name;
        return map;
    }

    /// <summary>Whether the control itself is shown, apart from its window being hidden (every control of a hidden
    /// window reports Visible false).</summary>
    private static bool OwnVisible(Control c)
    {
        try
        {
            var m = typeof(Control).GetMethod("GetState", BindingFlags.Instance | BindingFlags.NonPublic);
            if (m is not null && m.GetParameters() is [{ ParameterType: var pt }] && pt.IsEnum)
                return (bool)m.Invoke(c, [Enum.ToObject(pt, 0x00000002)])!;
        }
        catch { /* fall through */ }
        return true;
    }

    // ---------------- doing it the way a person does ----------------

    /// <summary>What a mouse click or the space bar does: the control's own OnClick, so a tick box toggles and a
    /// button fires (and closes its dialog, if it has a DialogResult) through the handlers the app wired up. Called
    /// directly because PerformClick refuses a control whose window is hidden, which in a headless run is all of them.</summary>
    private static void UserClick(Control c)
    {
        if (c is LinkLabel link && link.Links.Count > 0)
        {
            CallProtected(link, "OnLinkClicked", new LinkLabelLinkClickedEventArgs(link.Links[0]));
            return;
        }
        CallProtected(c, "OnClick", EventArgs.Empty);
    }

    /// <summary>What leaving a field does once it has been typed into.</summary>
    private static void FinishEdit(Control c)
    {
        CallProtected(c, "OnValidating", new CancelEventArgs());
        CallProtected(c, "OnValidated", EventArgs.Empty);
        CallProtected(c, "OnLeave", EventArgs.Empty);
    }

    private static void CallProtected(object target, string method, EventArgs args)
    {
        var m = target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, [args.GetType()], null)
                ?? target.GetType().GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                    .FirstOrDefault(x => x.Name == method && x.GetParameters() is [{ } p] && p.ParameterType.IsInstanceOfType(args));
        if (m is null) throw new MissingMethodException(target.GetType().Name, method);
        try { m.Invoke(target, [args]); }
        catch (TargetInvocationException ex) when (ex.InnerException is not null) { throw ex.InnerException; }
    }

    private static bool TryOnOff(string value, out bool on)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "on": case "true": case "yes": case "1": case "tick": case "ticked": on = true; return true;
            case "off": case "false": case "no": case "0": case "untick": case "unticked": on = false; return true;
            default: on = false; return false;
        }
    }

    private static bool TryParseKeys(string text, out Keys keys)
    {
        keys = Keys.None;
        foreach (var part in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl": case "control": keys |= Keys.Control; continue;
                case "shift": keys |= Keys.Shift; continue;
                case "alt": keys |= Keys.Alt; continue;
                case "space": keys |= Keys.Space; continue;
                case "enter": case "return": keys |= Keys.Enter; continue;
                case "esc": case "escape": keys |= Keys.Escape; continue;
                case "del": keys |= Keys.Delete; continue;
                case "pgup": keys |= Keys.PageUp; continue;
                case "pgdn": keys |= Keys.PageDown; continue;
            }
            if (part.Length == 1 && char.IsLetterOrDigit(part[0])) { keys |= (Keys)char.ToUpperInvariant(part[0]); continue; }
            if (Enum.TryParse<Keys>(part, ignoreCase: true, out var k)) { keys |= k; continue; }
            return false;
        }
        return (keys & Keys.KeyCode) != Keys.None;
    }

    /// <summary>Deliver a key press to a control through the same pre-processing a real one gets (dialog keys,
    /// mnemonics, the control's own key handling), with the modifier keys held down for the duration.</summary>
    private static void SendKeysTo(Control c, Keys keys)
    {
        var handle = c.Handle;
        var saved = new byte[256];
        GetKeyboardState(saved);
        var state = (byte[])saved.Clone();
        if ((keys & Keys.Control) != 0) state[(int)Keys.ControlKey] = 0x80;
        if ((keys & Keys.Shift) != 0) state[(int)Keys.ShiftKey] = 0x80;
        if ((keys & Keys.Alt) != 0) state[(int)Keys.Menu] = 0x80;
        SetKeyboardState(state);
        try
        {
            var vk = (int)(keys & Keys.KeyCode);
            var alt = (keys & Keys.Alt) != 0;
            var down = Message.Create(handle, alt ? WM_SYSKEYDOWN : WM_KEYDOWN, vk, 1);
            if (c.PreProcessControlMessage(ref down) != PreProcessControlState.MessageProcessed)
                SendMessage(handle, down.Msg, down.WParam, down.LParam);
            var ch = CharFor(keys);
            if (ch != '\0')
            {
                var charMsg = Message.Create(handle, WM_CHAR, ch, 1);
                if (c.PreProcessControlMessage(ref charMsg) != PreProcessControlState.MessageProcessed)
                    SendMessage(handle, charMsg.Msg, charMsg.WParam, charMsg.LParam);
            }
            // Alt with a letter or digit is what fires a control's Alt key, and Windows delivers that as a SYSTEM character,
            // not as the key going down. Without it Alt+M reached the control as a key and nothing listened (2026-09-24).
            var sysCh = SysCharFor(keys);
            if (sysCh != '\0')
            {
                // Bit 29 of the message's details says Alt is held, as it is for a real Alt+letter.
                var sysMsg = Message.Create(handle, WM_SYSCHAR, sysCh, unchecked((nint)0x20000001));
                if (c.PreProcessControlMessage(ref sysMsg) != PreProcessControlState.MessageProcessed)
                    SendMessage(handle, sysMsg.Msg, sysMsg.WParam, sysMsg.LParam);
            }
            SendMessage(handle, alt ? WM_SYSKEYUP : WM_KEYUP, vk, unchecked((nint)0xC0000001));
        }
        finally { SetKeyboardState(saved); }
    }

    /// <summary>The character Alt with a letter or digit sends, for a control's Alt key; nothing for anything else.</summary>
    private static char SysCharFor(Keys keys)
    {
        if ((keys & Keys.Alt) == 0 || (keys & Keys.Control) != 0) return '\0';
        var code = keys & Keys.KeyCode;
        if (code is >= Keys.A and <= Keys.Z) return (keys & Keys.Shift) != 0 ? (char)code : char.ToLowerInvariant((char)code);
        if (code is >= Keys.D0 and <= Keys.D9) return (char)('0' + (code - Keys.D0));
        return '\0';
    }

    private static char CharFor(Keys keys)
    {
        if ((keys & (Keys.Control | Keys.Alt)) != 0) return '\0';
        var code = keys & Keys.KeyCode;
        if (code == Keys.Space) return ' ';
        if (code == Keys.Enter) return '\r';
        if (code is >= Keys.A and <= Keys.Z) return (keys & Keys.Shift) != 0 ? (char)code : char.ToLowerInvariant((char)code);
        if (code is >= Keys.D0 and <= Keys.D9) return (char)('0' + (code - Keys.D0));
        return '\0';
    }

    // ---------------- words ----------------

    private static (string Verb, string Args) SplitVerb(string line)
    {
        line = line.Trim();
        var space = line.IndexOf(' ');
        return space < 0 ? (line.ToLowerInvariant(), "") : (line[..space].ToLowerInvariant(), line[(space + 1)..].Trim());
    }

    /// <summary>Words, with "double quoted" runs kept together.</summary>
    internal static List<string> Tokens(string text)
    {
        var tokens = new List<string>();
        foreach (Match m in Regex.Matches(text, "\"([^\"]*)\"|(\\S+)")) tokens.Add(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
        return tokens;
    }

    private static string Quote(string? s) => "\"" + (s ?? "") + "\"";

    private static string Short(string s)
    {
        s = s.Replace("\r", "").Replace("\n", " / ");
        return s.Length <= 120 ? s : s[..117] + "...";
    }

    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_CHAR = 0x0102;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const int WM_SYSCHAR = 0x0106;

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetKeyboardState(byte[] state);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetKeyboardState(byte[] state);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern nint SendMessage(nint h, int msg, nint w, nint l);
}
