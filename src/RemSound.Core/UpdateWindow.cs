namespace RemSound.Core;

/// <summary>
/// The "only install updates within this time range" option (Preferences → updates; feature request
/// 2026-07-26). RemSound streams live audio, so an automatic update mid-session kills someone's
/// sound; this gates AUTOMATIC installs (background poll + startup check) to a daily window — e.g.
/// 01:00 to 06:00 — so updates land overnight. A manual "Check for updates now" is deliberately NOT
/// gated: the user asking by hand knows what they're doing. Times are minutes-of-day in local time,
/// picked from 15-minute slots; a window whose end is at or before its start wraps past midnight
/// (22:00 → 06:00 covers late evening THROUGH early morning).
/// </summary>
public static class UpdateWindow
{
    public const int SlotMinutes = 15;
    public const int SlotsPerDay = 24 * 60 / SlotMinutes; // 96

    /// <summary>"HH:mm" for a minutes-of-day value (the Preferences list text).</summary>
    public static string FormatMinutes(int minutesOfDay) => $"{minutesOfDay / 60:D2}:{minutesOfDay % 60:D2}";

    /// <summary>Is this local time-of-day inside the window? Start inclusive, end exclusive.
    /// start == end means an empty selection — treated as "no restriction" rather than "never",
    /// because a window that silently blocks every update forever is a support trap.</summary>
    public static bool IsWithin(TimeSpan timeOfDay, int startMinutes, int endMinutes)
    {
        var m = (int)timeOfDay.TotalMinutes;
        if (startMinutes == endMinutes) return true;
        return startMinutes < endMinutes
            ? m >= startMinutes && m < endMinutes       // same-day window (01:00–06:00)
            : m >= startMinutes || m < endMinutes;      // wraps midnight (22:00–06:00)
    }

    /// <summary>How long from this time-of-day until the window next OPENS — drives the deferred
    /// retry timer, so an update found outside the window installs when the window starts instead
    /// of waiting for the next (possibly 24-hourly) poll to happen to land inside it.</summary>
    public static TimeSpan UntilNextStart(TimeSpan timeOfDay, int startMinutes)
    {
        var mins = startMinutes - timeOfDay.TotalMinutes;
        if (mins <= 0) mins += 24 * 60;
        return TimeSpan.FromMinutes(mins);
    }
}
