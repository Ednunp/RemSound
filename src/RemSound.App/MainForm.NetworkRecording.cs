using System.Globalization;
using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// Record &gt; Save the last minute of the network (2026-09-30, the simulator review's first improvement). The receiver keeps
/// the last minute of audio-packet arrivals (<see cref="RemSound.Receiver.NetworkRecorder"/>); this saves them to the logs
/// folder with what the simulator needs to play them back - the configuration, each lane's buffer and output period, the
/// codec, the ping to each sender - and what the receiver itself heard go wrong in that minute. Replayed with
/// RemSound.exe --simulate replay --recording &lt;file&gt;.
/// </summary>
public sealed partial class MainForm
{
    private ToolStripMenuItem BuildSaveNetworkItem()
    {
        var item = new ToolStripMenuItem("Save the last minute of the &network") { AccessibleName = "Save the last minute of the network" };
        item.Click += (_, _) => SaveNetworkRecording();
        return item;
    }

    /// <summary>Once a second (the streams tick): the receiver's counters, for a saved minute's live figures.</summary>
    private void NoteNetworkSecond() => receiver.NoteNetworkSecond();

    private void SaveNetworkRecording()
    {
        var (path, packets, streams) = WriteNetworkRecording();
        if (path is null)
        {
            AppMessageBox.Show(this, "Nothing has arrived in the last minute, so there is no network to save.", AppName,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        logFile.Event($"network recording saved: {path} ({packets} packets, {streams} stream(s)) [{ActiveAudioConfiguration().Describe()}]");
        AppMessageBox.Show(this, $"Saved the last minute of the network: {packets} packets from {streams} stream(s), to {Path.GetFileName(path)} "
            + "in the logs folder.", AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>Write the file; null when nothing arrived in the last minute.</summary>
    private (string? Path, int Packets, int Streams) WriteNetworkRecording()
    {
        string I(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);
        var facts = new List<KeyValuePair<string, string>>
        {
            new("machine", Environment.MachineName),
            new("saved", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
            new("RemSound", System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "?"),
            new("configuration", ActiveAudioConfiguration().Describe()),
            new("auto-tune ASIO", AutoTuneOnFor(RenderRoute.AsioLane) ? "on" : "off"),
            new("auto-tune WASAPI", AutoTuneOnFor(RenderRoute.WasapiLane) ? "on" : "off"),
            new("lowest-delay auto-tune", lowestDelayTune ? "on" : "off"),
            new("ASIO buffer setting frames", settings.LoadAsioBufferFrames() == 0 ? "the driver's own" : settings.LoadAsioBufferFrames().ToString(CultureInfo.InvariantCulture)),
            new("ASIO output period ms", I(RenderCbMsFor(RenderRoute.AsioLane))),
            new("WASAPI output period ms", I(RenderCbMsFor(RenderRoute.WasapiLane))),
        };
        // The ping to each peer the heartbeat knows: half of it is the best guess at the one-way delay a replay starts from.
        var pings = heartbeatService?.GetAllPeerHealth().Where(h => h.RttMs is not null).ToList() ?? [];
        foreach (var h in pings) facts.Add(new($"ping {h.AudioEndpoint.Address} ms", h.RttMs!.Value.ToString(CultureInfo.InvariantCulture)));
        if (pings.Count > 0) facts.Add(new("one-way ms", I(pings.Min(h => h.RttMs!.Value) / 2.0)));
        var rec = receiver.TakeNetworkRecording(facts);
        if (rec.Packets.Count == 0) return (null, 0, 0);
        var folder = (logFile.Path is { } log ? Path.GetDirectoryName(log) : null) ?? AppConfig.LogsDirectory;
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"RemSound-network-{Environment.MachineName}-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        rec.Save(path);
        return (path, rec.Packets.Count, rec.Streams.Count);
    }

    internal (string? Path, int Packets, int Streams) SaveNetworkRecordingForTest() => WriteNetworkRecording();
}
