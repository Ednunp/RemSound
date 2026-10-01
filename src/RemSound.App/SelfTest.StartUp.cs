using System.Reflection;
using RemSound.Core;

namespace RemSound.App;

internal static partial class SelfTest
{
    /// <summary>START-UP: the program's main thread is a single-threaded apartment, as Windows' file and folder pickers,
    /// the clipboard and drag-and-drop need. On 2026-09-27 a method was put between <c>[STAThread]</c> and Main, the
    /// attribute went with it, and every Windows picker then hung - the gate's first sign was a step that never finished.</summary>
    private static string StartUpThreadIsSta()
    {
        var main = typeof(Program).GetMethod("Main", BindingFlags.NonPublic | BindingFlags.Static);
        Check(main is not null, "Program.Main must be found");
        Check(main!.GetCustomAttribute<STAThreadAttribute>() is not null,
            "Program.Main must carry [STAThread] - without it Windows' pickers, the clipboard and drag-and-drop hang or fail");
        Check(Thread.CurrentThread.GetApartmentState() == ApartmentState.STA,
            $"and the thread the program started on must really be one ({Thread.CurrentThread.GetApartmentState()})");
        return "Program.Main carries [STAThread], and the program's own thread is a single-threaded apartment";
    }

    /// <summary>OPUS: the native libopus is the one the codec calls reach, and the log says so. Concentus falls back to its
    /// managed C# Opus without a word when it cannot find opus.dll beside the exe, and for months every clean publish left
    /// it out: a copy first started from anywhere but its own folder used the fallback, which repairs lost packets about 17
    /// cents off pitch (2026-09-28). The gate runs this from a folder that is not RemSound's own.</summary>
    private static string NativeOpusIsInUse()
    {
        Check(File.Exists(Path.Combine(AppContext.BaseDirectory, "opus.dll")),
            $"opus.dll must be beside the exe ({AppContext.BaseDirectory}) - Concentus looks nowhere else");
        var encoder = Concentus.OpusCodecFactory.CreateEncoder(48000, 2, Concentus.Enums.OpusApplication.OPUS_APPLICATION_RESTRICTED_LOWDELAY, TextWriter.Null);
        var decoder = Concentus.OpusCodecFactory.CreateDecoder(48000, 2, TextWriter.Null);
        var names = $"{encoder.GetType().Name}, {decoder.GetType().Name}";
        (encoder as IDisposable)?.Dispose();
        (decoder as IDisposable)?.Dispose();
        Check(names.Split(", ").All(n => n.Contains("Native", StringComparison.Ordinal)),
            $"the encoder and decoder must be the native libopus ones (got {names}) - the managed fallback repairs lost packets off pitch");
        Check(OpusInUse.IsNative && OpusInUse.Describe() == "native libopus in use", $"and the log line must say so (\"opus: {OpusInUse.Describe()}\")");
        return $"native libopus in use ({names}), opus.dll beside the exe, and the log says so";
    }

    /// <summary>START TO TRAY: a window starting straight in the tray (Start minimised, --minimized) is never on screen or
    /// in front on its way there, and comes back from the tray a whole, visible window. It used to come up in full, with
    /// the keyboard, for a moment at every such start (the gate's screen-reader watch, 2026-09-27).</summary>
    private static string StartToTrayIsQuiet()
    {
        var profile = Profile.NewBlank();
        profile.Password = RemSoundCrypto.Obfuscate("start-to-tray-test-password");
        MainForm? form = null;
        var restoreMinimised = MainForm.startNextInstanceMinimized;
        try
        {
            MainForm.startNextInstanceMinimized = true;
            try { form = new MainForm(null, profile, null, null, headless: true); }
            catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }
            form.Show();
            // Shown, and the move to the tray queued behind it: this is the moment it used to be seen, and heard.
            Check(form.QuietStartToTrayForTest && form.Opacity == 0,
                $"on its way to the tray the window must be see-through and not take the keyboard (opacity {form.Opacity})");
            for (var i = 0; i < 20 && form.Visible; i++) { Application.DoEvents(); Thread.Sleep(10); }
            Check(!form.Visible, "and it must then be in the tray");
            Check(!form.QuietStartToTrayForTest && form.Opacity == 1,
                $"and a normal window again, for whenever it is next shown (opacity {form.Opacity})");
            form.TrayControllerForTest.Restore();
            Application.DoEvents();
            Check(form.Visible && form.Opacity == 1 && form.WindowState != FormWindowState.Minimized,
                "and Show RemSound from the tray must bring back a whole, visible window");
            return "shown see-through and without the keyboard on its way to the tray, a normal window once there, and whole again from the tray";
        }
        finally
        {
            MainForm.startNextInstanceMinimized = restoreMinimised;
            try { form?.Dispose(); } catch { /* teardown */ }
        }
    }

    /// <summary>CODEC LIST: Opus live latency first and what a new profile starts with, then Opus broadcast quality, then
    /// uncompressed PCM (Ed, 2026-09-28) - and a profile saved with any choice opens as that choice, wherever it now sits
    /// in the list: they are matched by codec and frame, never by position.</summary>
    private static string CodecListOrderAndDefault()
    {
        var fresh = Profile.NewBlank();
        Check(fresh.Codec == AudioTransportCodec.Opus && fresh.OpusFrameSamplesPerChannel == 120,
            $"a new profile must start with Opus live latency (it has {fresh.Codec}, {fresh.OpusFrameSamplesPerChannel} samples)");
        var profile = Profile.NewBlank();
        profile.Password = RemSoundCrypto.Obfuscate("codec-list-test-password");
        MainForm? form = null;
        try
        {
            try { form = new MainForm(null, profile, null, null, headless: true); }
            catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }
            var box = Require(FieldOf<ComboBox>(form, "codecBox"), "the main window has no codecBox field any more");
            var labels = box.Items.Cast<object>().Select(i => i.ToString() ?? "").ToList();
            Check(labels.Count == 3 && labels[0].StartsWith("Opus, live latency", StringComparison.Ordinal)
                  && labels[1].StartsWith("Opus, broadcast quality", StringComparison.Ordinal) && labels[2].StartsWith("PCM", StringComparison.Ordinal),
                $"the list must run live latency, broadcast quality, PCM (it runs: {string.Join(" | ", labels)})");
            Check(box.SelectedIndex == 0, $"a new profile must open on live latency, the first choice (it is on {box.SelectedItem})");
            foreach (var (codec, frames, want) in new[]
                     {
                         (AudioTransportCodec.Pcm, 480, "PCM"), (AudioTransportCodec.Pcm, 120, "PCM"),
                         (AudioTransportCodec.Opus, 960, "Opus, broadcast quality"), (AudioTransportCodec.Opus, 480, "Opus, broadcast quality"),
                         (AudioTransportCodec.Opus, 240, "Opus, broadcast quality"), (AudioTransportCodec.Opus, 120, "Opus, live latency"),
                     })
            {
                var got = form.CodecChoiceForTest(codec, frames).Label;
                Check(got.StartsWith(want, StringComparison.Ordinal), $"a profile saved as {codec} at {frames} samples must open as {want} (it opens as {got})");
            }
            return "live latency, broadcast quality, PCM, in that order; a new profile starts on live latency; every saved choice opens as itself";
        }
        finally
        {
            try { form?.Dispose(); } catch { /* teardown */ }
        }
    }

    /// <summary>OPEN RECORDINGS FOLDER: Ctrl+Shift+O opens the recordings folder from anywhere in the main window, as
    /// Command-Shift-O does on the Mac (Andre, 2026-09-28). Pressed through the control channel's key command, which goes
    /// through the same key handling a real press does, from a list and a text box on one tab and a list on another; File
    /// Explorer is stood in for, so nothing opens. The log must say which folder, and the Record menu must show the keys.</summary>
    private static string OpenRecordingsFolderShortcut()
    {
        var restoreMuted = CuePlayer.GloballyMuted;
        CuePlayer.GloballyMuted = true;
        Windowless.InstallHook();
        Windowless.SetActiveForTest(true);
        var scratch = Path.Combine(Path.GetTempPath(), "remsound-openfolder-" + Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(scratch, "my recordings");
        var profile = Profile.NewBlank();
        profile.RecordingSettings = new RecordingSettings { Folder = folder };
        MainForm? form = null;
        try
        {
            try { form = new MainForm(null, profile, null, null, headless: true); }
            catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }
            _ = form.Handle;
            var main = form;
            var opened = new List<string>();
            var logged = new List<string>();
            main.RecordingControllerForTest.OpenInExplorerForTest = f => { lock (opened) opened.Add(f); };
            main.LogForTest.EventTapForTest = line => { lock (logged) logged.Add(line); };
            var engine = new RemoteControlEngine(() => main);

            var item = Require(main.MainMenuStrip?.Items.OfType<ToolStripMenuItem>().SelectMany(m => m.DropDownItems.OfType<ToolStripMenuItem>())
                .FirstOrDefault(i => i.AccessibleName == "Open current recordings folder"), "the Record menu has no Open current recordings folder item");
            Check(item.ShortcutKeys == (Keys.Control | Keys.Shift | Keys.O) && item.ShowShortcutKeys,
                $"the menu item must carry and show Ctrl+Shift+O, so a screen reader reads it with the item (it has {item.ShortcutKeys})");

            DriveWhilePumping(form, () =>
            {
                foreach (var (tab, control) in new[] { ("Connectivity", "Connected peers"), ("Connectivity", "Peer details"), ("Audio profile", "Buffer smoothness") })
                {
                    Check(main.Invoke(() => engine.Execute($"tab \"{tab}\"")).StartsWith("ok", StringComparison.Ordinal), $"the {tab} tab must be reachable");
                    int before;
                    lock (opened) before = opened.Count;
                    var said = main.Invoke(() => engine.Execute($"key \"{control}\" ctrl+shift+o"));
                    Check(said.StartsWith("ok", StringComparison.Ordinal), $"the key command must be taken on {control} ({said})");
                    Check(WaitFor(() => { lock (opened) return opened.Count == before + 1; }, TimeSpan.FromSeconds(5)),
                        $"Ctrl+Shift+O on {control} ({tab} tab) must open the recordings folder, once ({opened.Count - before} times)");
                    lock (opened)
                        Check(string.Equals(opened[^1], folder, StringComparison.OrdinalIgnoreCase), $"and it must be the profile's folder ({opened[^1]})");
                }
                var plain = main.Invoke(() => engine.Execute("key \"Buffer smoothness\" shift+o"));
                Check(plain.StartsWith("ok", StringComparison.Ordinal), $"Shift+O must be taken too ({plain})");
                lock (opened) Check(opened.Count == 3, $"Shift+O without Ctrl must not open the recordings folder ({opened.Count} opens)");
            }, TimeSpan.FromSeconds(30));
            Check(Directory.Exists(folder), "a folder that did not exist yet must be made before it is opened");
            lock (logged)
                Check(logged.Count(l => l.Contains("opened the recordings folder", StringComparison.Ordinal) && l.Contains(folder, StringComparison.OrdinalIgnoreCase)) == 3,
                    $"the log must say each time which folder was opened ({logged.Count(l => l.Contains("recordings folder", StringComparison.Ordinal))} lines)");
            return "Ctrl+Shift+O opens the profile's recordings folder from a list and a text box on one tab and a list on another, "
                 + "making it first; the menu shows the keys; the log names the folder; Shift+O alone does not";
        }
        finally
        {
            try { form?.Dispose(); } catch { /* teardown */ }
            Windowless.SetActiveForTest(false);
            CuePlayer.GloballyMuted = restoreMuted;
            try { Directory.Delete(scratch, recursive: true); } catch { /* scratch */ }
        }
    }
}
