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
    private bool loadAttempted;
    private bool loaded;

    public bool Speak(string text, bool interrupt = true)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (!EnsureLoaded()) return false;
        try { return Tolk_Output(text, interrupt); }
        catch { return false; }
    }

    public void Shutdown()
    {
        lock (sync)
        {
            if (!loaded) return;
            try { Tolk_Unload(); } catch { /* best-effort */ }
            loaded = false;
            loadAttempted = false;
        }
    }

    /// <summary>Load Tolk once. Succeeds only if Tolk loads AND reports a working speech channel, so a
    /// machine with the DLLs present but no screen reader running stays silent instead of half-init'd.</summary>
    private bool EnsureLoaded()
    {
        lock (sync)
        {
            if (loaded) return true;
            if (loadAttempted) return false;   // already tried and failed — don't spam load attempts
            loadAttempted = true;
            try { loaded = Tolk_Load() && Tolk_HasSpeech(); }
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
