namespace RemSound.Core;

/// <summary>
/// THE READINGS FILE (2026-10-22, issue 35, Jonathan Schuster: 6.2.2's logs "still a lot"). The numbers RemSound measures
/// every second used to share the log with its events, and were 98.8% of it. They now go to a CSV file of their own beside
/// the log, which opens in Excel, and the log keeps what happened.
///
/// <para>What reaches the file:</para>
/// <list type="bullet">
/// <item>A MINUTE row for every minute, summing the whole minute up: counts added, peaks the highest, low points the lowest,
/// levels averaged, running totals as they ended (<see cref="ReadingAgg"/>). 6.2.2 wrote one second's readings a minute,
/// so a spike between them was missed.</item>
/// <item>SECOND rows only around a problem (<see cref="Capture"/>): seconds wait <see cref="Memory"/> in memory, and a
/// capture keeps the ones it reaches back to and the next <see cref="After"/>. The rest are let go.</item>
/// </list>
///
/// <para>Rows go out when they are <see cref="Memory"/> old, which is what keeps the file in time order while a capture can
/// still reach five minutes back - so the file runs five minutes behind until RemSound closes, logging is switched off or
/// Write logs now is pressed (<see cref="Flush"/>). A minute row carries the time the minute ended.</para>
///
/// <para>Not thread-safe: each log calls it holding its own write lock.</para>
/// </summary>
public sealed class ReadingsRecorder
{
    public static readonly TimeSpan Memory = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan After = TimeSpan.FromSeconds(30);

    private readonly ReadingsSchema schema;
    private readonly Action<string> writeLine;
    private readonly LinkedList<Pending> pending = new();
    private readonly Dictionary<(string Stream, string Output), Summary> minute = new();
    private DateTime? minuteStart;
    private DateTime captureUntil = DateTime.MinValue;
    private bool headerWritten;

    private sealed class Pending(DateTime at, string line, bool isMinute)
    {
        public DateTime At { get; } = at;
        public string Line { get; } = line;
        public bool IsMinute { get; } = isMinute;
        public bool Captured { get; set; }
    }

    public ReadingsRecorder(ReadingsSchema schema, Action<string> writeLine)
    {
        this.schema = schema;
        this.writeLine = writeLine;
    }

    /// <summary>How many rows wait in memory (the gate's view).</summary>
    public int Waiting => pending.Count;

    /// <summary>One second: the machine-wide row and a row for each stream on each output.</summary>
    public void Second(DateTime now, ReadingsRow machine, IReadOnlyList<ReadingsRow>? streams = null)
    {
        var thisMinute = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, now.Kind);
        if (minuteStart is { } open && open != thisMinute)
        {
            // The minute ended when the next began - or now, if the clock went back, so the file stays in time order.
            var end = open.AddMinutes(1);
            CloseMinute(now < end ? now : end);
        }
        minuteStart ??= thisMinute;

        var captured = now <= captureUntil;
        Add(machine, now, captured);
        if (streams is not null) foreach (var row in streams) Add(row, now, captured);
        Emit(now - Memory);
    }

    private void Add(ReadingsRow row, DateTime now, bool captured)
    {
        var key = (row.Stream, row.Output);
        if (!minute.TryGetValue(key, out var summary)) minute[key] = summary = new Summary(schema, row.Stream, row.Output);
        summary.Add(row);
        pending.AddLast(new Pending(now, row.Line(now, "second"), isMinute: false) { Captured = captured });
    }

    private void CloseMinute(DateTime end)
    {
        foreach (var summary in minute.Values) pending.AddLast(new Pending(end, summary.Line(end), isMinute: true));
        minute.Clear();
        minuteStart = null;
    }

    /// <summary>Keep the seconds from <paramref name="lookBack"/> ago and the next <see cref="After"/>. Returns the span the
    /// file will hold, for the log line that points at it.</summary>
    public (DateTime From, DateTime To) Capture(DateTime now, TimeSpan lookBack)
    {
        var from = now;
        for (var node = pending.Last; node is not null; node = node.Previous)
        {
            if (node.Value.At < now - lookBack) break;
            if (node.Value.IsMinute) continue;
            node.Value.Captured = true;
            from = node.Value.At;
        }
        if (now + After > captureUntil) captureUntil = now + After;
        return (from, captureUntil);
    }

    /// <summary>Write everything that is going to be written, now: every minute row so far and every captured second. With
    /// <paramref name="closeMinute"/> (RemSound closing) the minute still open is summed up too, as far as it got.</summary>
    public void Flush(DateTime now, bool closeMinute = false)
    {
        if (closeMinute && minuteStart is not null) CloseMinute(now);
        Emit(DateTime.MaxValue);
    }

    /// <summary>Logging switched off: nothing waiting may reach the file later.</summary>
    public void Forget()
    {
        pending.Clear();
        minute.Clear();
        minuteStart = null;
        captureUntil = DateTime.MinValue;
    }

    private void Emit(DateTime upTo)
    {
        while (pending.First is { } node && node.Value.At <= upTo)
        {
            pending.RemoveFirst();
            if (!node.Value.IsMinute && !node.Value.Captured) continue;
            if (!headerWritten)
            {
                writeLine(schema.Header());
                headerWritten = true;
            }
            writeLine(node.Value.Line);
        }
    }

    /// <summary>The header row, for a file that is started again (a new part).</summary>
    public string HeaderLine => schema.Header();

    /// <summary>One stream's (or the machine's) minute, as its seconds come in.</summary>
    private sealed class Summary(ReadingsSchema schema, string stream, string output)
    {
        private readonly double[] sum = new double[schema.Columns.Count];
        private readonly double?[] value = new double?[schema.Columns.Count];
        private readonly int[] count = new int[schema.Columns.Count];
        private readonly string?[] text = new string?[schema.Columns.Count];

        public void Add(ReadingsRow row)
        {
            for (var i = 0; i < schema.Columns.Count; i++)
            {
                var column = schema.Columns[i];
                if (column.Text)
                {
                    if (row.Texts[i] is { } t) text[i] = t;
                    continue;
                }
                if (row.Numbers[i] is not { } n) continue;
                count[i]++;
                switch (column.Agg)
                {
                    case ReadingAgg.Sum: value[i] = (value[i] ?? 0) + n; break;
                    case ReadingAgg.Max: value[i] = value[i] is { } m && m >= n ? m : n; break;
                    case ReadingAgg.Min: value[i] = value[i] is { } m2 && m2 <= n ? m2 : n; break;
                    case ReadingAgg.Mean: sum[i] += n; value[i] = sum[i] / count[i]; break;
                    case ReadingAgg.Last: value[i] = n; break;
                }
            }
        }

        public string Line(DateTime end)
        {
            var row = new ReadingsRow(schema, schema.Columns.Select(c => c.Group)) { Stream = stream, Output = output };
            for (var i = 0; i < schema.Columns.Count; i++)
            {
                row.Numbers[i] = value[i];
                row.Texts[i] = text[i];
            }
            return row.Line(end, "minute");
        }
    }
}
