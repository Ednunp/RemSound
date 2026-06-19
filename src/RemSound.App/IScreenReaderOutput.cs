namespace RemSound.App;

/// <summary>
/// Abstraction over a screen-reader speech backend, so RemSound can speak text straight to whatever
/// screen reader is running (NVDA, JAWS, SAPI, ...). Needed for feedback the screen reader can't
/// otherwise observe — most importantly a GLOBAL hotkey firing while RemSound isn't focused, where
/// NVDA reads nothing on its own.
///
/// Kept as an interface purely so the concrete backend can be swapped later without touching callers.
/// Today the only implementation is <see cref="TolkScreenReaderOutput"/>, which works on every
/// Windows version RemSound supports (including Windows 7). Prism (evaluated 2026-06-19) is the more
/// modern option but hard-requires Windows 10+, so it can't replace Tolk while Win7 is supported — if
/// that changes, add a PrismScreenReaderOutput and pick it per-OS in <see cref="ScreenReader"/>.
/// </summary>
internal interface IScreenReaderOutput
{
    /// <summary>Speak <paramref name="text"/> through the active screen reader. <paramref name="interrupt"/>
    /// true cuts off whatever it's currently saying. Returns true if the text was handed to a screen
    /// reader, false if none is available. Best-effort — never throws.</summary>
    bool Speak(string text, bool interrupt = true);

    /// <summary>Release the backend. Safe to call more than once.</summary>
    void Shutdown();
}
