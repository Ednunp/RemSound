using System.Runtime.InteropServices;

namespace RemSound.App;

/// <summary>
/// Tolk-backed <see cref="IScreenReaderOutput"/>. Tolk (https://github.com/dkager/tolk, LGPL-3.0) is a
/// screen-reader abstraction DLL that auto-detects whichever reader is running (NVDA, JAWS, Window-Eyes,
/// SuperNova, System Access, ZoomText) and falls back to SAPI, then routes speech to it. The native
/// <c>Tolk.dll</c> plus its helpers (<c>nvdaControllerClient64.dll</c>, <c>SAAPI64.dll</c>) ship flat
/// next to the exe (see the .csproj Content items) — Tolk.dll loads those helpers from the same folder.
///
/// Loaded lazily on the first <see cref="Speak"/> and never re-attempted once it fails, so a missing DLL
/// or absent screen reader just means silence, never an exception. All entry points are wrapped in
/// try/catch and guarded by a lock, so it's safe to call from any thread (e.g. a hotkey callback).
/// </summary>
internal sealed class TolkScreenReaderOutput : IScreenReaderOutput
{
    private readonly object sync = new();
    private bool loaded;            // Tolk loaded AND a screen reader speaking
    private bool tolkLoaded;        // Tolk itself loaded, screen reader or not
    private bool dllMissing;        // Tolk.dll is not there: nothing will ever speak, so stop looking
    private long lastAttemptMs = long.MinValue;

    /// <summary>How long after finding no screen reader RemSound looks again, the next time it has something to say.</summary>
    internal static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(5);

    /// <summary>Gate seams: stand-ins for Tolk's calls and for the clock, so the looking again can be driven with no
    /// screen reader and no DLL. <see cref="AttemptsForTest"/> counts the looks.</summary>
    internal Func<bool>? LoadForTest;
    internal Func<bool>? HasSpeechForTest;
    internal Func<string, bool>? OutputForTest;
    internal Func<long>? ClockForTest;
    internal int AttemptsForTest;

    public bool Speak(string text, bool interrupt = true)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (!EnsureLoaded()) return false;
        try { return OutputForTest?.Invoke(text) ?? Tolk_Output(text, interrupt); }
        catch { return false; }
    }

    public void Shutdown()
    {
        lock (sync)
        {
            if (tolkLoaded && LoadForTest is null)
            {
                try { Tolk_Unload(); } catch { /* best-effort */ }
            }
            tolkLoaded = false;
            loaded = false;
            lastAttemptMs = long.MinValue;
        }
    }

    /// <summary>Is a screen reader there to speak? Tolk is loaded once; the screen reader is looked for again when it was
    /// not there, at most every <see cref="RetryAfter"/>. It used to be looked for once: if RemSound first spoke while NVDA
    /// was not running - RemSound starting at sign-in before NVDA, or NVDA restarting after the audio service took its
    /// speech - everything RemSound says stayed silent for the rest of the session (2026-09-25 sweep).</summary>
    private bool EnsureLoaded()
    {
        lock (sync)
        {
            if (loaded) return true;
            if (dllMissing) return false;
            var now = ClockForTest?.Invoke() ?? Environment.TickCount64;
            if (lastAttemptMs != long.MinValue && now - lastAttemptMs < (long)RetryAfter.TotalMilliseconds) return false;
            lastAttemptMs = now;
            AttemptsForTest++;
            try
            {
                if (!tolkLoaded) tolkLoaded = LoadForTest?.Invoke() ?? Tolk_Load();
                loaded = tolkLoaded && (HasSpeechForTest?.Invoke() ?? Tolk_HasSpeech());
            }
            catch (DllNotFoundException) { dllMissing = true; loaded = false; }
            catch { loaded = false; }
            return loaded;
        }
    }

    [DllImport("Tolk.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Tolk_Load();

    [DllImport("Tolk.dll")]
    private static extern void Tolk_Unload();

    [DllImport("Tolk.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Tolk_HasSpeech();

    [DllImport("Tolk.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Tolk_Output(string text, [MarshalAs(UnmanagedType.Bool)] bool interrupt);
}
