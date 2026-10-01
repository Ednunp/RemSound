using RemSound.Core;

namespace RemSound.App;

internal static partial class SelfTest
{
    /// <summary>Steps that finished with more threads holding Windows' audio priority (MMCSS) than they started with, and
    /// how many. Windows lets one process hold it on 32 threads at once; the full gate of 2026-10-01 reached that by the
    /// time SERVER PATH ran, so its thread was refused the priority it checks for and the step failed for the earlier
    /// steps' sake.</summary>
    private static readonly List<string> boostLeaks = [];

    /// <summary>After a step: anything it left boosted, given a second to let go (a thread gives its registration back as
    /// it ends, which can be just after its owner is disposed).</summary>
    private static void NoteBoostsLeftBy(string step, int before)
    {
        var deadline = Environment.TickCount64 + 1000;
        while (WindowsAudioThreadBoost.LiveRegistrations > before && Environment.TickCount64 < deadline) Thread.Sleep(20);
        var left = WindowsAudioThreadBoost.LiveRegistrations - before;
        if (left > 0) lock (boostLeaks) boostLeaks.Add($"{step} (+{left}, {WindowsAudioThreadBoost.LiveRegistrations} held after it)");
    }

    /// <summary>GATE GUARD: no step leaves threads holding Windows' audio priority. A step that leaves its sender's or
    /// receiver's threads running keeps their MMCSS registrations, and 32 of them make Windows refuse every later step's
    /// audio thread - which fails a step that checks its own priority, and runs the rest slower than the app ever does.</summary>
    private static string? NoStepLeftAudioThreadsBoosted()
    {
        string[] leaks;
        lock (boostLeaks) leaks = boostLeaks.ToArray();
        Check(leaks.Length == 0,
            $"{leaks.Length} step(s) left threads holding Windows' audio priority (32 is all a process may hold): {string.Join("; ", leaks.Take(20))}");
        return $"no step left a thread holding Windows' audio priority ({WindowsAudioThreadBoost.LiveRegistrations} held now)";
    }
}
