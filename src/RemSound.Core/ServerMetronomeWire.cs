using System.Buffers.Binary;

namespace RemSound.Core;

/// <summary>One member's beat grid from the server: whether it plays, whether this member's delay has been measured yet,
/// whether this member leads it and who does (an empty id: nobody), and the beat - in THIS member's own clock, already early
/// by this member's delay to the server, so a note played on it reaches the server on the beat.</summary>
public sealed record ServerGrid(bool Running, bool Measured, bool YouLead, Guid Master, MetronomeTimeline Timeline);

/// <summary>
/// The server's metronome on the wire (2026-09-29, PLAN-metronome-server-2026-09-29.md): relay type 13, group-framed, read by
/// the relay itself and never passed on - so NOT sealed with the password, which the relay does not know. Nothing in it is a
/// secret: capture times, a tempo, a beat grid. The relay takes it only from a member at the address it has checked, a
/// command only from the member that leads the metronome, and sends a grid only to members that have sent a report - a
/// RemSound that knows this type. The layout matches server/remsound-relay.py (TIMING_*), little-endian throughout.
/// </summary>
public static class ServerMetronomeWire
{
    public const byte TypeTiming = 13;
    public const byte Report = 1;
    public const byte Claim = 2;
    public const byte Release = 3;
    public const byte Set = 4;
    public const byte Grid = 20;
    public const int MaxReportEntries = 16;
    public const int ReportEntryBytes = 14;
    public const int GridBytes = 40;
    private const byte FlagRunning = 1, FlagMeasured = 2, FlagYouLead = 4;

    /// <summary>When packets were captured, in this machine's metronome clock (microseconds): [kind][count] then each
    /// (stream u16, sequence u32, captured i64). The relay pairs each with when that packet reached it.</summary>
    public static byte[] BuildReport(IReadOnlyList<(ushort Stream, uint Sequence, long CapturedUs)> entries)
    {
        var count = Math.Min(entries.Count, MaxReportEntries);
        var body = new byte[2 + count * ReportEntryBytes];
        body[0] = Report;
        body[1] = (byte)count;
        for (var i = 0; i < count; i++)
        {
            var at = body.AsSpan(2 + i * ReportEntryBytes);
            BinaryPrimitives.WriteUInt16LittleEndian(at, entries[i].Stream);
            BinaryPrimitives.WriteUInt32LittleEndian(at[2..], entries[i].Sequence);
            BinaryPrimitives.WriteInt64LittleEndian(at[6..], entries[i].CapturedUs);
        }
        return body;
    }

    public static byte[] BuildClaim() => [Claim];

    public static byte[] BuildRelease() => [Release];

    /// <summary>The master's metronome, for the server: playing or not, the tempo in thousandths of a beat a minute, the
    /// signature. The server starts it a moment ahead, or changes it at the next bar line.</summary>
    public static byte[] BuildSet(bool running, uint milliBpm, byte beatsPerBar, byte beatUnit)
    {
        var body = new byte[8];
        body[0] = Set;
        body[1] = running ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(2), milliBpm);
        body[6] = beatsPerBar;
        body[7] = beatUnit;
        return body;
    }

    /// <summary>A grid as the relay writes it: [kind][flags][master id 16][version u32][milli-bpm u32][per bar u8][unit u8]
    /// [anchor beat u32][anchor time i64]. False for anything else, or one that cannot mean a beat.</summary>
    public static bool TryReadGrid(ReadOnlySpan<byte> body, out ServerGrid grid)
    {
        grid = null!;
        if (body.Length < GridBytes || body[0] != Grid) return false;
        var flags = body[1];
        var master = new Guid(body.Slice(2, 16), bigEndian: true);
        var version = BinaryPrimitives.ReadUInt32LittleEndian(body[18..]);
        var milliBpm = BinaryPrimitives.ReadUInt32LittleEndian(body[22..]);
        var perBar = body[26];
        var unit = body[27];
        var anchorBeat = BinaryPrimitives.ReadUInt32LittleEndian(body[28..]);
        var anchorUs = BinaryPrimitives.ReadInt64LittleEndian(body[32..]);
        var timeline = new MetronomeTimeline(version, (flags & FlagRunning) != 0, milliBpm, perBar, unit, anchorBeat, anchorUs);
        if (!timeline.IsValid) return false;
        grid = new ServerGrid((flags & FlagRunning) != 0, (flags & FlagMeasured) != 0, (flags & FlagYouLead) != 0, master, timeline);
        return true;
    }

    /// <summary>A grid in the relay's framing, as the relay would send it to <paramref name="recipient"/> - for the gate.</summary>
    public static byte[] BuildGridForTest(ServerGrid grid)
    {
        var body = new byte[GridBytes];
        body[0] = Grid;
        body[1] = (byte)((grid.Running ? FlagRunning : 0) | (grid.Measured ? FlagMeasured : 0) | (grid.YouLead ? FlagYouLead : 0));
        grid.Master.TryWriteBytes(body.AsSpan(2), bigEndian: true, out _);
        var t = grid.Timeline;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(18), t.Version);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(22), t.MilliBpm);
        body[26] = t.BeatsPerBar;
        body[27] = t.BeatUnit;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(28), (uint)t.AnchorBeat);
        BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(32), t.AnchorTimeUs);
        return body;
    }
}
