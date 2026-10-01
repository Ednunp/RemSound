using System.Text;
using RemSound.Core;
using NAudio.Wave;

namespace RemSound.App;

internal static partial class SelfTest
{
    internal const string SoundPackAuthor = "Ed Pourtahmasbi";
    internal const string SoundPackName = "RemSound application default sound pack";

    /// <summary>SHIPPED SOUNDS (2026-09-28). Ed asked for every default sound to name him as the author and the RemSound
    /// default sound pack, in place of the sound libraries, keywords, editor data and an email address some carried. Every
    /// WAV beside the exe: 16-bit stereo 44.1 kHz, opens and has sound, holds only its format, its audio and one INFO block,
    /// and that block credits Ed and the pack. The check is first shown to fail on a copy carrying a library's credit.</summary>
    private static string? ShippedSoundsCreditEd()
    {
        var dir = AppConfig.SoundsDirectory;
        // Every folder under it too: the metronome's clicks live in their own (2026-09-28).
        var files = Directory.GetFiles(dir, "*.wav", SearchOption.AllDirectories);
        Check(files.Length > 0, $"no sounds in {dir}");

        // A shipped sound given a library's credit and a broadcast-wave block must be caught.
        var scratch = Path.Combine(Path.GetTempPath(), "remsound-sound-credit-" + Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            var planted = WavChunks(File.ReadAllBytes(files[0]))
                .Select(c => c.Id == "LIST" ? ("LIST", InfoBlock(("IART", "SoundMorph"), ("ICMT", "Solo Beeps 93"))) : c)
                .Append(("bext", new byte[602]))
                .ToList();
            File.WriteAllBytes(scratch, RiffOf(planted));
            var problems = SoundCreditProblems(scratch);
            Check(problems.Any(p => p.Contains("SoundMorph")) && problems.Any(p => p.Contains("bext")),
                $"a sound crediting a library, with a bext block, must be caught ({string.Join("; ", problems)})");
        }
        finally
        {
            try { File.Delete(scratch); } catch { }
        }

        var found = files.SelectMany(f => SoundCreditProblems(f).Select(p => Path.GetFileName(f) + ": " + p)).ToList();
        Check(found.Count == 0, string.Join("; ", found.Take(6)) + (found.Count > 6 ? $" and {found.Count - 6} more" : ""));
        return $"{files.Length} sounds, each 16-bit stereo 44.1 kHz, crediting {SoundPackAuthor} and the default sound pack, and nothing else";
    }

    /// <summary>What is wrong with one shipped sound, in words; empty when nothing is.</summary>
    internal static List<string> SoundCreditProblems(string path)
    {
        var problems = new List<string>();
        var chunks = WavChunks(File.ReadAllBytes(path));
        var ids = chunks.Select(c => c.Id).ToList();
        if (!ids.SequenceEqual(["fmt ", "data", "LIST"]))
            problems.Add($"holds {string.Join(", ", ids.Select(i => i.Trim()))}, where only fmt, data and one INFO block belong");
        var info = chunks.Where(c => c.Id == "LIST" && Encoding.ASCII.GetString(c.Body, 0, 4) == "INFO")
            .SelectMany(c => InfoFields(c.Body)).ToDictionary(f => f.Id, f => f.Text);
        if (info.GetValueOrDefault("IART") != SoundPackAuthor) problems.Add($"author is \"{info.GetValueOrDefault("IART")}\"");
        if (info.GetValueOrDefault("ICMT") != SoundPackName) problems.Add($"comment is \"{info.GetValueOrDefault("ICMT")}\"");
        if (info.GetValueOrDefault("IPRD") != SoundPackName) problems.Add($"pack is \"{info.GetValueOrDefault("IPRD")}\"");
        foreach (var (id, text) in info.Where(f => f.Key is not ("INAM" or "IART" or "ICMT" or "IPRD" or "ICOP")))
            problems.Add($"carries {id} \"{text}\"");
        try
        {
            using var reader = new WaveFileReader(path);
            var f = reader.WaveFormat;
            if (f.Encoding != WaveFormatEncoding.Pcm || f.BitsPerSample != 16 || f.Channels != 2 || f.SampleRate != 44100)
                problems.Add($"is {f.BitsPerSample}-bit, {f.Channels} channels, {f.SampleRate} Hz, not 16-bit stereo 44.1 kHz");
            if (reader.Length == 0) problems.Add("has no sound");
        }
        catch (Exception ex)
        {
            problems.Add("does not open: " + ex.Message);
        }
        return problems;
    }

    private static List<(string Id, byte[] Body)> WavChunks(byte[] b)
    {
        var list = new List<(string, byte[])>();
        for (var pos = 12; pos + 8 <= b.Length;)
        {
            var size = BitConverter.ToInt32(b, pos + 4);
            list.Add((Encoding.ASCII.GetString(b, pos, 4), b[(pos + 8)..Math.Min(b.Length, pos + 8 + size)]));
            pos += 8 + size + (size & 1);
        }
        return list;
    }

    private static IEnumerable<(string Id, string Text)> InfoFields(byte[] list)
    {
        for (var pos = 4; pos + 8 <= list.Length;)
        {
            var size = BitConverter.ToInt32(list, pos + 4);
            yield return (Encoding.ASCII.GetString(list, pos, 4), Encoding.ASCII.GetString(list, pos + 8, size).TrimEnd('\0'));
            pos += 8 + size + (size & 1);
        }
    }

    private static byte[] InfoBlock(params (string Id, string Text)[] fields)
    {
        var body = new List<byte>(Encoding.ASCII.GetBytes("INFO"));
        foreach (var (id, text) in fields)
        {
            var data = Encoding.ASCII.GetBytes(text + "\0").ToList();
            if (data.Count % 2 == 1) data.Add(0);
            body.AddRange(Encoding.ASCII.GetBytes(id));
            body.AddRange(BitConverter.GetBytes(data.Count));
            body.AddRange(data);
        }
        return [.. body];
    }

    private static byte[] RiffOf(List<(string Id, byte[] Body)> chunks)
    {
        var body = new List<byte>(Encoding.ASCII.GetBytes("WAVE"));
        foreach (var (id, data) in chunks)
        {
            body.AddRange(Encoding.ASCII.GetBytes(id));
            body.AddRange(BitConverter.GetBytes(data.Length));
            body.AddRange(data);
            if (data.Length % 2 == 1) body.Add(0);
        }
        return [.. Encoding.ASCII.GetBytes("RIFF"), .. BitConverter.GetBytes(body.Count), .. body];
    }
}
