using System.Globalization;
using System.Text;

namespace RemSound.Core;

/// <summary>
/// A minute of the network as a receiver met it (2026-09-30, the simulator's first improvement: "real networks,
/// replayed"). Every audio packet's arrival - when, which stream, its sequence number and size - with the facts needed to
/// replay it: what each stream was, the buffers and configuration, and what the receiver itself heard go wrong over the
/// same minute (so the simulator can be checked against it). Saved as plain text: facts, streams, then one line a packet.
/// </summary>
public sealed class NetworkRecording
{
    public const string FirstLine = "# RemSound network recording 1";

    /// <summary>One stream heard: where from, its stream id, and what its Format packet said (empty if none came).</summary>
    public sealed record Stream(int Index, string From, ushort StreamId, string Lane, string Format, int Codec, int FrameSamples);

    /// <summary>One packet: milliseconds after the recording's first packet, which stream (by index), its sequence and size.</summary>
    public readonly record struct Packet(double Ms, int Stream, uint Sequence, int Bytes);

    /// <summary>Facts in the order they were given: "machine", "saved", the configuration, the buffers, the live figures.</summary>
    public List<KeyValuePair<string, string>> Facts { get; } = new();
    public List<Stream> Streams { get; } = new();
    public List<Packet> Packets { get; } = new();

    public string? Fact(string key) => Facts.FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase)).Value;

    public void AddFact(string key, string value) => Facts.Add(new(key, value.Replace('\n', ' ').Replace('\r', ' ')));

    /// <summary>A fact read as a number, or null when it is missing or not one.</summary>
    public double? Number(string key) =>
        double.TryParse(Fact(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    public void Save(TextWriter w)
    {
        w.WriteLine(FirstLine);
        foreach (var (k, v) in Facts) w.WriteLine($"{k}: {v}");
        foreach (var s in Streams)
            w.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"stream {s.Index}: from {s.From} id {s.StreamId} lane {s.Lane} codec {s.Codec} frame {s.FrameSamples} format {s.Format}"));
        w.WriteLine("packets: ms stream sequence bytes");
        var sb = new StringBuilder();
        foreach (var p in Packets)
        {
            sb.Clear();
            sb.Append(p.Ms.ToString("0.000", CultureInfo.InvariantCulture)).Append(' ').Append(p.Stream).Append(' ')
              .Append(p.Sequence.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(p.Bytes.ToString(CultureInfo.InvariantCulture));
            w.WriteLine(sb.ToString());
        }
    }

    public void Save(string path)
    {
        using var w = new StreamWriter(path, false, new UTF8Encoding(false));
        Save(w);
    }

    public static NetworkRecording Load(string path)
    {
        using var r = new StreamReader(path);
        return Load(r);
    }

    /// <summary>Read a recording back. Anything that is not one throws, saying which line.</summary>
    public static NetworkRecording Load(TextReader r)
    {
        var rec = new NetworkRecording();
        if (r.ReadLine() != FirstLine) throw new FormatException("not a RemSound network recording (the first line is wrong)");
        var n = 1;
        var inPackets = false;
        string? line;
        while ((line = r.ReadLine()) is not null)
        {
            n++;
            if (line.Length == 0) continue;
            if (inPackets)
            {
                var p = line.Split(' ');
                if (p.Length != 4
                    || !double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var ms)
                    || !int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var stream)
                    || !uint.TryParse(p[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seq)
                    || !int.TryParse(p[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytes)
                    || stream < 0 || stream >= rec.Streams.Count)
                    throw new FormatException($"line {n} is not a packet: \"{line}\"");
                rec.Packets.Add(new Packet(ms, stream, seq, bytes));
                continue;
            }
            if (line.StartsWith("packets:", StringComparison.Ordinal)) { inPackets = true; continue; }
            if (line.StartsWith("stream ", StringComparison.Ordinal) && ParseStream(line) is { } s)
            {
                if (s.Index != rec.Streams.Count) throw new FormatException($"line {n}: streams must be numbered from 0 in order");
                rec.Streams.Add(s);
                continue;
            }
            var colon = line.IndexOf(": ", StringComparison.Ordinal);
            if (colon <= 0) throw new FormatException($"line {n} is neither a fact nor a stream: \"{line}\"");
            rec.Facts.Add(new(line[..colon], line[(colon + 2)..]));
        }
        if (!inPackets) throw new FormatException("the recording has no packets line");
        return rec;
    }

    private static Stream? ParseStream(string line)
    {
        // stream 0: from 10.77.0.1:50123 id 4282 lane Mixed codec 1 frame 120 format 48000 Hz, ...
        var colon = line.IndexOf(": ", StringComparison.Ordinal);
        if (colon < 0 || !int.TryParse(line.AsSpan(7, colon - 7), NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)) return null;
        var rest = line[(colon + 2)..];
        string? Word(string name)
        {
            var at = rest.IndexOf(name + " ", StringComparison.Ordinal);
            if (at < 0) return null;
            var start = at + name.Length + 1;
            var end = rest.IndexOf(' ', start);
            return end < 0 ? rest[start..] : rest[start..end];
        }
        var formatAt = rest.IndexOf("format ", StringComparison.Ordinal);
        if (Word("from") is not { } from || !ushort.TryParse(Word("id"), out var id) || Word("lane") is not { } lane
            || !int.TryParse(Word("codec"), out var codec) || !int.TryParse(Word("frame"), out var frame) || formatAt < 0) return null;
        return new Stream(index, from, id, lane, rest[(formatAt + 7)..], codec, frame);
    }
}
