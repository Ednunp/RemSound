using System.Globalization;
using System.Text;

namespace RemSound.Core;

/// <summary>How a reading is summed up for a minute row (ReadingsRecorder): a count since the last row is added up, a peak
/// takes the highest, a low point the lowest, a level its average, and a running total or a setting its last value.</summary>
public enum ReadingAgg { Sum, Max, Min, Mean, Last }

/// <summary>One column of a readings file: its name (the CSV header), how a minute row sums it up, and what it belongs to -
/// a reading is written only while the thing it measures is in use (Ed, 2026-10-20: "maybe they only get logged when they
/// get turned on"). An empty group is always in use. A text column keeps its last value.</summary>
public sealed record ReadingColumn(string Name, ReadingAgg Agg, string Group = "", bool Text = false);

/// <summary>The columns of one readings file, in order. The file's first columns are always Time, Kind ("second" or
/// "minute"), Stream and Output; a machine-wide row leaves the last two empty, a stream's row fills them.</summary>
public sealed class ReadingsSchema
{
    public IReadOnlyList<ReadingColumn> Columns { get; }
    private readonly Dictionary<string, int> index;

    public ReadingsSchema(IEnumerable<ReadingColumn> columns)
    {
        Columns = columns.ToList();
        index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < Columns.Count; i++)
        {
            if (!index.TryAdd(Columns[i].Name, i)) throw new ArgumentException($"the column {Columns[i].Name} is named twice");
        }
    }

    public bool TryIndex(string name, out int i) => index.TryGetValue(name, out i);

    public string Header() => "Time,Kind,Stream,Output," + string.Join(",", Columns.Select(c => Csv(c.Name)));

    /// <summary>A CSV field: quoted when it holds a comma, a quote or a line break (a device or peer name can), with any
    /// quote doubled - so a name can never break a row (OWASP's log-injection warning).</summary>
    public static string Csv(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        if (text.IndexOfAny([',', '"', '\n', '\r']) < 0) return text;
        return "\"" + text.Replace("\"", "\"\"").Replace('\r', ' ').Replace('\n', ' ') + "\"";
    }

    public static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>
/// One second's readings, machine-wide or for one stream on one output. A value is kept only when its column's group is in
/// use this second (<see cref="ReadingColumn.Group"/>), so an output, a send lane or the plugin that is not in use leaves its
/// cells empty - and a zero on something that IS in use stays, meaning measured and clean. A name the schema does not know
/// is noted in <see cref="Unknown"/> (the gate fails on any) rather than thrown on the once-a-second tick.
/// </summary>
public sealed class ReadingsRow
{
    public ReadingsSchema Schema { get; }
    public string Stream { get; init; } = "";
    public string Output { get; init; } = "";
    private readonly HashSet<string> inUse;
    internal readonly double?[] Numbers;
    internal readonly string?[] Texts;
    public List<string> Unknown { get; } = [];

    public ReadingsRow(ReadingsSchema schema, IEnumerable<string> groupsInUse)
    {
        Schema = schema;
        inUse = new HashSet<string>(groupsInUse, StringComparer.Ordinal) { "" };
        Numbers = new double?[schema.Columns.Count];
        Texts = new string?[schema.Columns.Count];
    }

    public bool InUse(string group) => inUse.Contains(group);

    public ReadingsRow Set(string name, double value)
    {
        if (!Schema.TryIndex(name, out var i)) { Unknown.Add(name); return this; }
        if (inUse.Contains(Schema.Columns[i].Group)) Numbers[i] = value;
        return this;
    }

    public ReadingsRow Set(string name, bool value) => Set(name, value ? 1 : 0);

    public ReadingsRow Text(string name, string? value)
    {
        if (!Schema.TryIndex(name, out var i)) { Unknown.Add(name); return this; }
        if (inUse.Contains(Schema.Columns[i].Group)) Texts[i] = value ?? "";
        return this;
    }

    /// <summary>The value written for a column, or null when the cell is empty (a test's view).</summary>
    public string? Cell(string name)
    {
        if (!Schema.TryIndex(name, out var i)) return null;
        return Schema.Columns[i].Text ? Texts[i] : Numbers[i] is { } n ? ReadingsSchema.Number(n) : null;
    }

    internal string Line(DateTime time, string kind)
    {
        var sb = new StringBuilder(64 + Numbers.Length * 4);
        sb.Append(time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(',').Append(kind)
          .Append(',').Append(ReadingsSchema.Csv(Stream)).Append(',').Append(ReadingsSchema.Csv(Output));
        for (var i = 0; i < Numbers.Length; i++)
        {
            sb.Append(',');
            if (Schema.Columns[i].Text) sb.Append(ReadingsSchema.Csv(Texts[i]));
            else if (Numbers[i] is { } n) sb.Append(ReadingsSchema.Number(n));
        }
        return sb.ToString();
    }
}
