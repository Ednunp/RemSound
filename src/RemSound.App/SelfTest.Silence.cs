using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace RemSound.App;

/// <summary>
/// SILENT AND INVISIBLE (Ed, 2026-09-27: the gate and a headless copy "should be silent and invisible", after NVDA read
/// out the gate's password dialogs and menus while he worked). The gate's watcher (tools\GateTools watch) listens from
/// outside to what a screen reader hears; these check the rules from inside, where the watcher cannot see - speech goes
/// straight to NVDA through Tolk, not through any window.
/// </summary>
internal static partial class SelfTest
{
    /// <summary>SILENT: a --silent or --headless start says nothing and plays nothing, and every word RemSound says goes
    /// through the one switch that silences it.</summary>
    private static string SilentStartsSayNothing()
    {
        var speech = ScreenReader.Suppressed;
        var cues = CuePlayer.GloballyMuted;
        var silent = Windowless.SilentLaunch;
        try
        {
            foreach (var (args, name) in new[] { (new[] { "--silent" }, "--silent"), (new[] { "--headless", "--config-dir", "x" }, "--headless") })
            {
                ScreenReader.Suppressed = false;
                CuePlayer.GloballyMuted = false;
                Windowless.SilentLaunch = false;
                var headless = Program.ApplyQuietFlags(args);
                Check(ScreenReader.Suppressed, $"a {name} start must say nothing through the screen reader - a --silent start went on speaking (2026-09-27)");
                Check(CuePlayer.GloballyMuted, $"a {name} start must play no cue sounds");
                Check(headless == (name == "--headless"), $"a {name} start must be taken for {(name == "--headless" ? "" : "not ")}a headless one");
            }
            ScreenReader.Suppressed = false;
            CuePlayer.GloballyMuted = false;
            Check(!Program.ApplyQuietFlags(["--minimized"]) && !ScreenReader.Suppressed && !CuePlayer.GloballyMuted,
                "an ordinary start must keep its speech and its sounds");
        }
        finally
        {
            ScreenReader.Suppressed = speech;
            CuePlayer.GloballyMuted = cues;
            Windowless.SilentLaunch = silent;
        }

        // Every way of speaking goes through ScreenReader, which the switch silences: Tolk's output is called in one file,
        // and Windows' spoken notifications in one place.
        var root = FindSourceRoot();
        if (root is null) return Skip("the starts are silent, but the source tree is not reachable to check every way of speaking (set REMSOUND_SOURCE_ROOT)");
        var speakers = new Dictionary<string, List<string>>();
        foreach (var project in new[] { "RemSound.App", "RemSound.Core", "RemSound.Sender", "RemSound.Receiver", "RemSound.Ui", "RemSound.Plugin" })
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src", project), "*.cs", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(file);
                if (name.StartsWith("SelfTest", StringComparison.Ordinal) || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) continue;
                var text = File.ReadAllText(file);
                foreach (var way in new[] { "Tolk_Output(", "Tolk_Speak(", "RaiseAutomationNotification(", "nvdaController_speakText", "SpeechSynthesizer" })
                    if (text.Contains(way, StringComparison.Ordinal)) (speakers.TryGetValue(way, out var l) ? l : speakers[way] = []).Add(name);
            }
        Check(speakers.GetValueOrDefault("Tolk_Output(")?.SequenceEqual(["TolkScreenReaderOutput.cs"]) == true,
            $"Tolk may only be called from TolkScreenReaderOutput, behind ScreenReader's switch (found in: {string.Join(", ", speakers.GetValueOrDefault("Tolk_Output(") ?? [])})");
        Check(speakers.GetValueOrDefault("RaiseAutomationNotification(")?.SequenceEqual(["ScreenReader.cs"]) == true,
            $"a spoken notification may only be raised from ScreenReader.Notify, behind the switch (found in: {string.Join(", ", speakers.GetValueOrDefault("RaiseAutomationNotification(") ?? [])})");
        foreach (var way in new[] { "Tolk_Speak(", "nvdaController_speakText", "SpeechSynthesizer" })
            Check(!speakers.ContainsKey(way), $"nothing may speak around the switch with {way} (found in: {string.Join(", ", speakers.GetValueOrDefault(way) ?? [])})");
        // And the switch itself, driven (it read the two methods' source until 2026-09-28): with it down, neither speech
        // nor a spoken notification gets out.
        var reachedBefore = Interlocked.Read(ref ScreenReader.ReachedForTest);
        using (var label = new Label { Text = "Self-test" })
        {
            _ = label.Handle;
            Check(!ScreenReader.Speak("this must never be heard"), "with the switch down, Speak must say it said nothing");
            ScreenReader.Notify(label, "this must never be heard");
        }
        Check(Interlocked.Read(ref ScreenReader.ReachedForTest) == reachedBefore, "ScreenReader.Speak and Notify must both stop at the switch - something got out");
        return "--silent and --headless starts say nothing and play nothing, an ordinary start keeps both; Tolk and spoken "
             + "notifications are reached only through ScreenReader, which stops at the switch";
    }

    /// <summary>INVISIBLE: a window made while hiding stays off every screen and is never always-on-top - however it is
    /// centred, moved or raised - and the owner a dialog is given to come to the front is not always-on-top either.</summary>
    private static string HiddenWindowsStayOutOfSight()
    {
        Windowless.InstallHook();
        Windowless.SetActiveForTest(true);
        try
        {
            using var form = new Form { Text = "Silent check", StartPosition = FormStartPosition.CenterScreen, TopMost = true, ShowInTaskbar = false, Size = new Size(300, 200) };
            form.Show();
            Application.DoEvents();
            Check(OffEveryScreen(form.Handle), $"a centred window must stay off every screen while hiding ({RectOf(form.Handle)})");
            Check(!IsTopMost(form.Handle), "and must not be always-on-top: a screen reader takes every event from an always-on-top window");
            form.Location = new Point(100, 100);
            Application.DoEvents();
            Check(OffEveryScreen(form.Handle), $"moved onto the screen, it must go straight back off ({RectOf(form.Handle)})");
            form.TopMost = false;
            form.TopMost = true;
            Application.DoEvents();
            Check(!IsTopMost(form.Handle), "made always-on-top again, it must lose it again");

            using var owned = new Form { Text = "Silent check, owned", StartPosition = FormStartPosition.CenterParent, ShowInTaskbar = false };
            owned.Show(form);
            Application.DoEvents();
            Check(OffEveryScreen(owned.Handle) && !IsTopMost(owned.Handle), $"a window it owns, centred on it, must stay off screen and not always-on-top ({RectOf(owned.Handle)})");
        }
        finally
        {
            Windowless.SetActiveForTest(false);
        }
        return "a hidden window, centred, moved on screen and made always-on-top, stays off every screen and never always-on-top; so does a window it owns";
    }

    private static bool OffEveryScreen(IntPtr handle)
    {
        if (!NativeGetWindowRect(handle, out var r)) return false;
        var rect = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        return Screen.AllScreens.All(s => !s.Bounds.IntersectsWith(rect));
    }

    private static string RectOf(IntPtr handle) => NativeGetWindowRect(handle, out var r) ? $"{r.Left},{r.Top} to {r.Right},{r.Bottom}" : "no rectangle";

    private static bool IsTopMost(IntPtr handle) => (NativeGetWindowLong(handle, -20) & 0x8) != 0;

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", EntryPoint = "GetWindowRect")] private static extern bool NativeGetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll", EntryPoint = "GetWindowLong")] private static extern int NativeGetWindowLong(IntPtr hwnd, int index);

    /// <summary>NOTHING REACHED THE SCREEN READER (2026-09-28, from the review of every step). The no-noise guard proves the
    /// switches are down and nothing goes round them - but not that nothing got through: a focus change, a tick box's state
    /// or a spoken notification reach NVDA without any speech call. This runs at the end and counts what actually went
    /// out, the whole run through: speech, Windows' spoken notifications, and the accessibility events RemSound raises
    /// itself. (The run is on a desktop of its own as well, which NVDA does not watch; this is the check from inside.)</summary>
    private static string? NothingInTheRunReachedTheScreenReader()
    {
        Check(ScreenReader.Suppressed, "speech must still be held silent at the end of the run - a step left it on");
        Check(WinEventNotifier.Quiet && Windowless.QuietForRun, "the accessibility events must still be held quiet at the end of the run - a step left them on");
        var said = Interlocked.Read(ref ScreenReader.ReachedForTest);
        var raised = Interlocked.Read(ref WinEventNotifier.RaisedForTest);
        Check(said == 0, $"NOT A WORD: {said} time(s) in this run, speech or a spoken notification went out to the screen reader");
        Check(raised == 0, $"NOT AN EVENT: {raised} accessibility event(s) went out to the screen reader in this run - a focus change or a tick box's state, read out");
        return "nothing reached the screen reader in the whole run: no speech, no spoken notification, no accessibility event";
    }
}
