using System.Text.RegularExpressions;

namespace RemSound.App;

/// <summary>
/// Guards on the GATE ITSELF. Everything else in this suite watches the product; these two watch the
/// watchers, because on 2026-08-23/24 four steps were found that did not do what their names said and
/// three of the four turned up by accident. A gate full of tests that cannot fail is worse than no
/// gate: it reports safety it has not established.
///
/// <para>Guard A (here) — no silent reflection. A reflection lookup that misses must THROW, not skip.
/// Two of the four bad tests called <c>GetMethod("SaveAudioMode")?.Invoke(...)</c> for a method
/// deleted in May 2026: the null-conditional silently did nothing, both passes ran against the
/// unchanged default, and one of those tests is named "in all THREE audio configurations".</para>
///
/// <para>Guard B lives in <c>SelfTest.cs</c> rather than here, because it has to wrap every step:
/// the assertion counter in <c>Check</c>/<c>Require</c>, snapshotted around each <c>RunStep</c>, so a
/// step that finishes having asserted nothing is failed instead of passed.</para>
/// </summary>
internal static partial class SelfTest
{
    /// <summary>The reflection lookups whose result must never be reached through a null-conditional.</summary>
    private static readonly string[] ReflectionLookups =
        ["GetMethod", "GetField", "GetProperty", "GetEvent", "GetConstructor", "GetNestedType"];

    private static string? GateHasNoSilentReflection()
    {
        var root = FindSourceRoot();
        if (root is null)
            return Skip("the source tree is not reachable from here (set REMSOUND_SOURCE_ROOT, as run-tests.ps1 does)");

        var appDir = Path.Combine(root, "src", "RemSound.App");
        var files = Directory.GetFiles(appDir, "SelfTest*.cs", SearchOption.TopDirectoryOnly)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        Check(files.Count > 0, $"no gate source found under {appDir} — this guard has stopped working, which makes it worse than useless");

        var offences = new List<string>();
        var lookupsSeen = 0;
        var guardedByRequire = 0;

        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            var name = Path.GetFileName(file);

            // Shape 1 — the direct one that actually bit us: Get…("name", …)?. …
            // Found by walking the balanced parentheses rather than by regex, because the argument
            // list contains parentheses of its own (BindingFlags expressions, typeof(...)) and a
            // regex that stops at the first ')' reads the wrong closing bracket.
            foreach (var start in LookupCallSites(text))
            {
                lookupsSeen++;
                var close = MatchingParen(text, start.OpenParen);
                if (close < 0) continue;
                var after = close + 1;
                while (after < text.Length && char.IsWhiteSpace(text[after])) after++;
                if (after + 1 < text.Length && text[after] == '?' && text[after + 1] == '.')
                    offences.Add($"{name}:{LineOf(text, start.NameIndex)} — {start.Lookup}(\"{start.Argument}\")?. "
                               + "silently does nothing when the lookup misses");

                var windowStart = Math.Max(0, start.NameIndex - 120);
                if (text[windowStart..start.NameIndex].Contains("Require(", StringComparison.Ordinal)) guardedByRequire++;
            }

            // Shape 2 — the indirect one: the lookup is assigned to a local that is later reached
            // through a null-conditional somewhere else in the file. Same silent no-op, spread over
            // two statements, and it would slip past shape 1 entirely.
            foreach (Match m in ReflectionLocalPattern().Matches(text))
            {
                var local = m.Groups["local"].Value;
                var statement = m.Value;
                // An explicit guard on the same statement is the whole point — those are fine.
                if (statement.Contains("?? throw", StringComparison.Ordinal) || statement.Contains("Require(", StringComparison.Ordinal))
                    continue;
                if (Regex.IsMatch(text, $@"(?<![A-Za-z0-9_]){Regex.Escape(local)}\s*\?\s*\."))
                    offences.Add($"{name}:{LineOf(text, m.Index)} — '{local}' comes from a reflection lookup and is later "
                               + "reached with '?.', so a missed lookup skips the check instead of failing it");
            }
        }

        Check(lookupsSeen > 0,
            "the scan found no reflection lookups anywhere in the gate — the scanner has broken, and a broken scanner "
            + "reports a clean bill of health over code it never read");

        Check(offences.Count == 0,
            "a reflection lookup in the gate must FAIL when it misses, never skip. Wrap it in Require(...) — "
            + $"these do not: {string.Join("; ", offences)}");

        return $"{lookupsSeen} reflection lookups across {files.Count} gate files, none reached through a null-conditional "
             + $"({guardedByRequire} guarded by Require at the call site)";
    }

    private readonly record struct LookupSite(string Lookup, string Argument, int NameIndex, int OpenParen);

    /// <summary>Every <c>GetMethod("…"</c> / <c>GetField("…"</c> / … call site, with the index of the
    /// opening parenthesis so the caller can find its true closing one.</summary>
    private static IEnumerable<LookupSite> LookupCallSites(string text)
    {
        foreach (var lookup in ReflectionLookups)
        {
            for (var i = text.IndexOf(lookup, StringComparison.Ordinal); i >= 0; i = text.IndexOf(lookup, i + 1, StringComparison.Ordinal))
            {
                // Must be a member access — otherwise the string literals in this very file (the
                // ReflectionLookups array, which is quoted) would count as call sites.
                if (i == 0 || text[i - 1] != '.') continue;
                var end = i + lookup.Length;
                if (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_')) continue;
                if (end >= text.Length) continue;
                var p = end;
                while (p < text.Length && char.IsWhiteSpace(text[p])) p++;
                if (p >= text.Length || text[p] != '(') continue;
                var q = p + 1;
                while (q < text.Length && char.IsWhiteSpace(text[q])) q++;
                if (q >= text.Length || text[q] != '"') continue;   // only the by-name lookups matter
                var argEnd = text.IndexOf('"', q + 1);
                if (argEnd < 0) continue;
                yield return new LookupSite(lookup, text[(q + 1)..argEnd], i, p);
            }
        }
    }

    private static int MatchingParen(string text, int openParen)
    {
        var depth = 0;
        for (var i = openParen; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '"':
                    i = text.IndexOf('"', i + 1);
                    if (i < 0) return -1;
                    break;
                case '(': depth++; break;
                case ')':
                    depth--;
                    if (depth == 0) return i;
                    break;
            }
        }
        return -1;
    }

    private static int LineOf(string text, int index) => text.AsSpan(0, index).Count('\n') + 1;

    /// <summary>A local declared from a reflection lookup, captured with the whole statement so an
    /// explicit guard on the same statement can be recognised.</summary>
    [GeneratedRegex(@"var\s+(?<local>[A-Za-z_][A-Za-z0-9_]*)\s*=[^;]*?\.Get(?:Method|Field|Property|Event|Constructor|NestedType)\s*\([^;]*?;",
        RegexOptions.Singleline)]
    private static partial Regex ReflectionLocalPattern();
}
