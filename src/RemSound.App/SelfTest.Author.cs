using System.Diagnostics;
using RemSound.Plugin;

namespace RemSound.App;

internal static partial class SelfTest
{
    internal const string AuthorName = "Ed Pourtahmasbi";

    /// <summary>WHO MADE IT (2026-09-28). Ed asked for his name, Ed Pourtahmasbi, where people look for an author: the About
    /// box (under the version), each program file's details in Windows (company, copyright), the publisher in Windows'
    /// installed apps list, the vendor a DAW shows beside the plugin, the licence and the readme. The plugin's identity is
    /// untouched (the known-answers step pins it); REAPER opening a v6.0 project with the new vendor was proved by hand.</summary>
    private static string? AuthorIsNamedEverywhere()
    {
        using (var about = new AboutDialog())
        {
            var heading = about.HeadingForTest!;
            var lines = heading.Text.Replace("\r\n", "\n").Split('\n');
            Check(lines.Length == 2 && lines[0].StartsWith("RemSound version ", StringComparison.Ordinal) && lines[1] == "Created by " + AuthorName,
                $"the About box must show the version and, under it, \"Created by {AuthorName}\" ({heading.Text.Replace("\r\n", " / ")})");
            Check(heading.AccessibleName == $"{lines[0]}, created by {AuthorName}",
                $"and a screen reader must be given both ({heading.AccessibleName})");
        }

        var files = new[] { Path.Combine(AppContext.BaseDirectory, "RemSound.exe") }
            .Concat(Directory.GetFiles(AppContext.BaseDirectory, "RemSound.*.dll"))
            .Concat(Directory.Exists(Path.Combine(AppContext.BaseDirectory, "plugin"))
                ? Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "plugin"), "RemSound.*.dll")
                : [])
            .ToList();
        Check(files.Count >= 6 && File.Exists(files[0]), $"premise: RemSound.exe and its libraries beside it ({files.Count} found)");
        foreach (var file in files)
        {
            var info = FileVersionInfo.GetVersionInfo(file);
            Check(info.CompanyName == AuthorName && info.LegalCopyright == $"Copyright (c) 2026 {AuthorName}",
                $"{Path.GetFileName(file)}'s details in Windows must name {AuthorName} as company and in the copyright (\"{info.CompanyName}\", \"{info.LegalCopyright}\")");
        }

        Check(AppInstaller.Publisher == AuthorName && AppInstaller.InstalledAppValues("x", "x", "1")["Publisher"] as string == AuthorName,
            $"Windows' installed apps list must show {AuthorName} as the publisher ({AppInstaller.Publisher})");

        var plugin = new RemSoundPlugin();
        try
        {
            Check(plugin.Company == AuthorName, $"the plugin's vendor, shown beside its name in a DAW, must be {AuthorName} ({plugin.Company})");
        }
        finally { try { plugin.CloseForTest(); } catch { } }

        var root = FindSourceRoot();
        if (root is null) return Skip("the program names Ed everywhere, but the source tree is not reachable to check the licence and readme (set REMSOUND_SOURCE_ROOT)");
        Check(File.ReadAllText(Path.Combine(root, "LICENSE")).Contains($"Copyright (c) 2026 {AuthorName}", StringComparison.Ordinal),
            $"the licence's copyright must be {AuthorName}'s");
        // Ed, 2026-09-28: "I think that's really nice and it gives you credit."
        Check(File.ReadAllText(Path.Combine(root, "README.md")).Contains($"Created by {AuthorName}, built with Claude Code.", StringComparison.Ordinal),
            $"the readme must say \"Created by {AuthorName}, built with Claude Code.\"");
        Check(File.ReadAllText(Path.Combine(root, "src", "RemSound.App", "AppInstaller.cs"))
                .Contains("foreach (var (name, value) in InstalledAppValues(installFolder, installedExe, CommandLine.AppVersion))", StringComparison.Ordinal),
            "the installed apps entry must be written from those values");
        return $"About box, {files.Count} program files, installed apps, the plugin's vendor, licence and readme all name {AuthorName}";
    }
}
