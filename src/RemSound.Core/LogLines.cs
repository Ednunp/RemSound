using System.Text.RegularExpressions;

namespace RemSound.Core;

/// <summary>How serious a log line is: the first word of every line, so a search for WARN or ERROR finds the problems.</summary>
public enum LogLevel { Info, Warn, Error }

/// <summary>
/// EVERY EVENT LINE STARTS WITH A SEVERITY WORD AND A CATEGORY (2026-10-22, issue 35): "WARN Peer: LAPTOP disconnected",
/// so a person, Notepad's search or findstr can pick out the problems, or everything about the server. The lines a change
/// writes on purpose say their level and category themselves; the hundreds written before this keep their words, and are
/// sorted here: a short "relay: ..." style start becomes the category (the server's is "Server", as the window calls it),
/// and a few plain words that mean trouble make a line a warning or an error.
/// </summary>
public static partial class LogLines
{
    public static string Word(LogLevel level) => level switch { LogLevel.Warn => "WARN", LogLevel.Error => "ERROR", _ => "INFO" };

    /// <summary>The start of a line written before categories existed, and the category it belongs to.</summary>
    private static readonly Dictionary<string, string> Prefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["relay"] = "Server", ["servers"] = "Server", ["server"] = "Server", ["server status"] = "Server",
        ["updater"] = "Updates", ["installer"] = "Install", ["power"] = "Power", ["upnp"] = "Router",
        ["ui"] = "Settings", ["preferences"] = "Settings", ["security"] = "Security", ["auto-tune"] = "Auto-tune",
        ["lowest-delay auto-tune"] = "Auto-tune", ["asio"] = "ASIO", ["discovery"] = "Peers", ["accept connections"] = "Peers",
        ["shaping"] = "Pan and EQ", ["sender"] = "Sender", ["receiver"] = "Receiver", ["recording"] = "Recording",
        ["auto-save"] = "Profile", ["save-profile"] = "Profile", ["remote-control"] = "Remote control",
        ["remote control"] = "Remote control", ["latency readout"] = "Latency", ["device-event"] = "Devices",
        ["device load"] = "Devices", ["hotkey"] = "Hotkeys", ["vst plugin"] = "Plugin", ["metronome"] = "Metronome",
        ["service"] = "Service", ["log housekeeping"] = "Log", ["web listener"] = "Web listener", ["close"] = "App",
    };

    /// <summary>The first word of a line with no such start, and its category.</summary>
    private static readonly Dictionary<string, string> FirstWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["profile"] = "Profile", ["unsaved"] = "Profile", ["renamed"] = "Profile", ["peer"] = "Peer", ["remembered"] = "Peers",
        ["named"] = "Peers", ["accept"] = "Peers", ["server"] = "Server", ["ASIO"] = "ASIO", ["audio"] = "Audio",
        ["first"] = "Audio", ["realtek"] = "Audio", ["tight"] = "Audio", ["Jamulus-style"] = "Audio", ["DAW"] = "Plugin",
        ["vst"] = "Plugin", ["plugin"] = "Plugin", ["update"] = "Updates", ["what's"] = "Updates", ["log"] = "Log",
        ["user"] = "Log", ["cue"] = "Sounds", ["web"] = "Web listener", ["push"] = "Push to talk", ["main"] = "Window",
        ["context"] = "Help", ["device"] = "Devices", ["hear"] = "Hear myself", ["longrun"] = "Long run",
        ["receiver"] = "Receiver", ["sender"] = "Sender", ["startup"] = "App", ["detail"] = "Log",
    };

    // A line about something going wrong. Checked on the words of lines that do not say their own level; deliberately
    // plain words, so a line is not called an error for mentioning one ("error=\"none\"" is not one).
    [GeneratedRegex(@"\b[A-Za-z]+Exception\b|\b(crash|crashed|fatal)\b|\bFAILED\b|\bthrew\b", RegexOptions.IgnoreCase)]
    private static partial Regex ErrorWords();

    [GeneratedRegex(@"\b(failed|failure|could not|couldn't|cannot|can't|refused|refuses|timed out|unreachable|stalled|deaf|mismatch|wrong password|out of date|too old|gave up|giving up|unavailable|faulted|disconnected|went quiet|no capture callbacks|overrun)\b", RegexOptions.IgnoreCase)]
    private static partial Regex WarnWords();

    // "relay: ...", "accept connections: ...", "vst plugin: ..." - up to three short words before a colon and a space.
    [GeneratedRegex(@"^(?<p>[A-Za-z][A-Za-z'\-]*(?: [A-Za-z][A-Za-z'\-]*){0,2}): (?<rest>.*)$", RegexOptions.Singleline)]
    private static partial Regex Prefixed();

    /// <summary>A line written before categories existed: its level from its words, its category from how it starts. A
    /// start the tables do not know becomes the category as it is ("Gate copy"), so its words still read the same.</summary>
    public static (LogLevel Level, string Category, string Text) Classify(string message)
    {
        var level = ErrorWords().IsMatch(message) ? LogLevel.Error : WarnWords().IsMatch(message) ? LogLevel.Warn : LogLevel.Info;
        if (Prefixed().Match(message) is { Success: true } m)
        {
            var prefix = m.Groups["p"].Value;
            var category = Prefixes.TryGetValue(prefix, out var known) ? known : char.ToUpperInvariant(prefix[0]) + prefix[1..];
            return (level, category, m.Groups["rest"].Value);
        }
        var first = message.Split(' ', 2)[0].TrimEnd(':', ',');
        return (level, FirstWords.TryGetValue(first, out var word) ? word : "App", message);
    }

    /// <summary>The line as written: the severity word, the time, then "Category: text". Tabs between, so a spreadsheet
    /// splits it; nothing in the text can start another line.</summary>
    public static string Format(LogLevel level, DateTime time, string category, string text) =>
        $"{Word(level)}\t{time:HH:mm:ss.fff}\t{category}: {Clean(text)}";

    public static string Clean(string text) => text.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
}
