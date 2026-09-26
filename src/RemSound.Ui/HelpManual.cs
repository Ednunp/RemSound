using System.Net;
using System.Text.RegularExpressions;

namespace RemSound.App;

/// <summary>
/// The manual's own words for one control, read straight out of readme.html (Ed, 2026-09-25: the help a control gives
/// is the manual's entry for it, so the two can never say different things).
///
/// <para>A control's entry is the element carrying <c>id="help-&lt;key&gt;"</c>: a row of one of the manual's control
/// tables, a paragraph, or a <c>div</c> around a heading and the paragraphs under it. It is turned into plain lines a
/// screen reader can arrow through: a table row reads as its name, then its shortcut, then what it does.</para>
///
/// <para>Here rather than in the app so the DAW plugin, which runs inside somebody else's program, can read its own
/// copy of the manual the same way.</para>
/// </summary>
internal static partial class HelpManual
{
    public const string IdPrefix = "help-";

    /// <summary>The id a help key is written under in the manual, and the anchor the browser opens it at.</summary>
    public static string AnchorFor(string key) => IdPrefix + key;

    /// <summary>How many elements carry this key's id. Exactly one is right.</summary>
    public static int CountOf(string html, string key) =>
        Regex.Matches(html, "\\bid=\"" + Regex.Escape(AnchorFor(key)) + "\"").Count;

    /// <summary>Every help key the manual carries, in the order they appear.</summary>
    public static IReadOnlyList<string> Keys(string html) =>
        HelpIdPattern().Matches(html).Select(m => m.Groups[1].Value).ToList();

    /// <summary>The whole element carrying this key's id, from its opening tag to its matching closing tag; null when
    /// there is none, or when it never closes.</summary>
    public static string? EntryHtml(string html, string key)
    {
        var open = Regex.Match(html, "<([a-zA-Z][a-zA-Z0-9]*)\\b[^>]*\\bid=\"" + Regex.Escape(AnchorFor(key)) + "\"[^>]*>");
        if (!open.Success) return null;
        var tag = open.Groups[1].Value;
        var tags = new Regex("<(/?)" + tag + "\\b[^>]*>", RegexOptions.IgnoreCase);
        var depth = 1;
        var at = open.Index + open.Length;
        while (depth > 0)
        {
            var t = tags.Match(html, at);
            if (!t.Success) return null;
            depth += t.Groups[1].Value == "/" ? -1 : 1;
            at = t.Index + t.Length;
        }
        return html[open.Index..at];
    }

    /// <summary>Open the manual in the default browser, at a help key's entry or at the top for null. A place in it goes
    /// through a one-line page that sends the browser on: Windows drops the "#place" from a file it opens by association,
    /// so readme.html#help-... would open at the top.</summary>
    public static void OpenInBrowser(string manualPath, string? key)
    {
        var open = manualPath;
        if (key is not null)
        {
            var target = new Uri(manualPath).AbsoluteUri + "#" + AnchorFor(key);
            var attribute = WebUtility.HtmlEncode(target);
            open = Path.Combine(Path.GetTempPath(), "RemSound manual link.html");
            File.WriteAllText(open,
                "<!doctype html><html><head><meta charset=\"utf-8\">"
                + $"<meta http-equiv=\"refresh\" content=\"0; url={attribute}\"><title>RemSound manual</title></head>"
                + $"<body><a href=\"{attribute}\">RemSound manual</a></body></html>");
        }
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(open) { UseShellExecute = true });
    }

    /// <summary>This key's entry as plain lines, or null when the manual has no entry for it.</summary>
    public static string? EntryText(string html, string key) => EntryHtml(html, key) is { } entry ? ToText(entry) : null;

    /// <summary>A piece of the manual as plain lines, joined with Windows line breaks.</summary>
    internal static string ToText(string fragment)
    {
        var s = fragment.TrimStart();
        // An entry that IS a row of a control table: its name, then its shortcut, then what it does, each a line.
        if (s.StartsWith("<tr", StringComparison.OrdinalIgnoreCase)) s = RowToLines(s);
        // A table inside an entry: each row as "heading: cell" lines, so a two-column table reads in pairs.
        s = TablePattern().Replace(s, m => TableToLines(m.Groups[1].Value));
        // The source's own line breaks are just spaces; the entry's lines come from its markup, below.
        s = WhitespacePattern().Replace(s, " ");
        s = LineBreakPattern().Replace(s, "\n");
        s = BlockTagPattern().Replace(s, "\n");
        s = AnyTagPattern().Replace(s, "");
        s = WebUtility.HtmlDecode(s);
        var lines = s.Split('\n')
            .Select(l => WhitespacePattern().Replace(l, " ").Trim())
            .Where(l => l.Length > 0);
        return string.Join("\r\n", lines);
    }

    private static string RowToLines(string row)
    {
        var cells = CellPattern().Matches(row).Select(c => Inline(c.Groups[1].Value)).ToList();
        return cells.Count switch
        {
            3 => $"<p>{cells[0]}</p><p>Shortcut: {cells[1]}</p><p>{cells[2]}</p>",
            _ => string.Concat(cells.Select(c => $"<p>{c}</p>")),
        };
    }

    private static string TableToLines(string table)
    {
        var rows = RowPattern().Matches(table).Select(r => r.Groups[1].Value).ToList();
        var headings = new List<string>();
        var lines = new List<string>();
        foreach (var row in rows)
        {
            var cells = CellPattern().Matches(row).Select(c => Inline(c.Groups[1].Value)).ToList();
            if (row.Contains("<th", StringComparison.OrdinalIgnoreCase)) { headings = cells; continue; }
            for (var i = 0; i < cells.Count; i++)
                lines.Add(i < headings.Count && headings[i].Length > 0 ? $"{headings[i]}: {cells[i]}" : cells[i]);
        }
        return string.Concat(lines.Select(l => $"<p>{l}</p>"));
    }

    /// <summary>One cell's words on one line, its own markup kept only as far as the later passes need.</summary>
    private static string Inline(string html) => WhitespacePattern().Replace(AnyTagPattern().Replace(html, ""), " ").Trim();

    [GeneratedRegex("\\bid=\"help-([^\"]+)\"")]
    private static partial Regex HelpIdPattern();
    [GeneratedRegex("<table\\b[^>]*>(.*?)</table>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex TablePattern();
    [GeneratedRegex("<tr\\b[^>]*>(.*?)</tr>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex RowPattern();
    [GeneratedRegex("<t[dh]\\b[^>]*>(.*?)</t[dh]>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex CellPattern();
    [GeneratedRegex("<br\\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreakPattern();
    [GeneratedRegex("</?(p|div|h[1-6]|li|ul|ol|tr|table|blockquote)\\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockTagPattern();
    [GeneratedRegex("<[^>]+>")]
    private static partial Regex AnyTagPattern();
    [GeneratedRegex("\\s+")]
    private static partial Regex WhitespacePattern();
}
