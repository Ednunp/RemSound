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
    public static bool Speak(string text, bool interrupt = true) => Backend.Speak(text, interrupt);

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
