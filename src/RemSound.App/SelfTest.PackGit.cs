using System.Diagnostics;

namespace RemSound.App;

internal static partial class SelfTest
{
    /// <summary>PACKS: GIT'S ANSWER (2026-09-29). The feature pack asks git which files changed. With many working-copy files
    /// in line endings git warns about, git wrote more warnings than a pipe holds; the pack read git's output to its end before
    /// reading its warnings, git waited for its warnings to be read, and the run hung for six hours before its first step. A
    /// stand-in for git writes 4,000 warning lines, then one file name: the answer must come back whole, within seconds.</summary>
    private static string? PackGitAnswerUnderWarnings()
    {
        var restore = GitStartForTest;
        try
        {
            GitStartForTest = (_, _) => new ProcessStartInfo("cmd.exe",
                "/c (for /l %i in (1,1,4000) do @echo warning: in the working copy of 'src/file%i.cs', LF will be replaced by CRLF 1>&2) & echo src/changed.cs");
            var clock = Stopwatch.StartNew();
            var answer = Task.Run(() => Git("C:\\", "diff --name-only HEAD"));
            Check(answer.Wait(TimeSpan.FromSeconds(20)), "git's answer must come back while it floods its warnings - it hung (the pipe was never read)");
            Check(answer.Result is { Count: 1 } lines && lines[0] == "src/changed.cs",
                $"the answer must be the one file name, whole - it was [{string.Join(", ", answer.Result ?? [])}]");
            return $"4,000 warning lines and one file name: the name read back in {clock.ElapsedMilliseconds} ms";
        }
        finally { GitStartForTest = restore; }
    }
}
