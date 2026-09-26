namespace RemSound.App;

/// <summary>
/// Process-wide screen-reader speech. Used for feedback the screen reader can't otherwise observe —
/// chiefly the "speak the status line" hotkey (GitHub issue #13), which must read aloud even when the
/// global hotkey fires while RemSound isn't the focused window.
///
/// Holds a single backend, created once on first use. Today that is always Tolk, which works on every
/// Windows version RemSound supports — including Windows 7. The interface seam exists so a future build
/// can choose a different backend per OS without changing any caller: branch inside
/// <see cref="CreateBackend"/>. (Prism is the modern successor to Tolk but requires Windows 10+, so it
/// can't be the default while Win7 is supported — see <see cref="IScreenReaderOutput"/>.)
/// </summary>
internal static class ScreenReader
{
    private static readonly object sync = new();
    private static IScreenReaderOutput? backend;

    /// <summary>
    /// Say nothing at all. The gate sets this for its whole run: a test that drives the window drives the code that
    /// speaks, and on 2026-09-20 Ed heard fragments of it — "password", "connected", "ticked us" — coming out of
    /// NVDA while a gate run was going on. A test that makes a noise on somebody's machine is a broken test, and
    /// speech is a noise like any other.
    /// </summary>
    internal static bool Suppressed { get; set; }

    private static IScreenReaderOutput Backend
    {
        get { lock (sync) { return backend ??= CreateBackend(); } }
    }

    private static IScreenReaderOutput CreateBackend()
    {
        // Win7-safe default. To adopt Prism on Windows 10+ later, branch on Environment.OSVersion
        // here and return a PrismScreenReaderOutput when the OS is >= Windows 10 — callers below stay
        // exactly as they are.
        return new TolkScreenReaderOutput();
    }

    /// <summary>Speak text through the active screen reader (best-effort; silent if none is running).
    /// Returns true if it reached a screen reader.</summary>
    public static bool Speak(string text, bool interrupt = true)
    {
        // A headless copy says nothing, but whoever drives it can read what it would have said (the speech command).
        HeadlessRecords.Note(HeadlessRecords.Speech, text);
        return !Suppressed && Backend.Speak(text, interrupt);
    }

    /// <summary>Have the screen reader read <paramref name="text"/> for a control, through Windows' own notification
    /// (NVDA reads it natively - not an extra speech layer). Behind the same switch as <see cref="Speak"/>: this was
    /// raised directly from the pan and EQ tab, where a run driving the window would have spoken it (2026-09-24).</summary>
    public static void Notify(System.Windows.Forms.Control control, string text)
    {
        HeadlessRecords.Note(HeadlessRecords.Speech, text);
        if (Suppressed || Windowless.Hiding) return;
        control.AccessibilityObject.RaiseAutomationNotification(
            System.Windows.Forms.Automation.AutomationNotificationKind.ActionCompleted,
            System.Windows.Forms.Automation.AutomationNotificationProcessing.MostRecent,
            text);
    }

    /// <summary>Release the backend on app shutdown. Safe to call when nothing was ever spoken.</summary>
    public static void Shutdown()
    {
        lock (sync)
        {
            backend?.Shutdown();
            backend = null;
        }
    }
}
