using RemSound.Core;

namespace RemSound.App;

internal static partial class SelfTest
{
    /// <summary>
    /// YOUR SEND IS RECORDED CLEANLY WHILE A DAW PLUGIN IS SENDING TOO — IN ALL THREE CONFIGURATIONS.
    ///
    /// <para>2026-09-13 review, finding 5. The recorder keeps one lock-free ring per sending lane, and a
    /// lock-free ring is only safe with ONE thread writing it. It chose the ring from the lane's route
    /// tag, and folded Mixed into the WASAPI ring on the reasoning that the classic modes only ever have
    /// one tap firing. The DAW plugin lane broke that: with an ASIO driver chosen it sends on Mixed while
    /// the WASAPI capture lane sends on WasapiLane, so two audio threads wrote one ring at once whenever
    /// you recorded your own send while a plugin was sending. On disk that is blocks lost, and blocks
    /// written one after another instead of mixed — the 2026-05-15 double-length garble back again.</para>
    ///
    /// <para><b>The routes come from a real sender</b>, set to the mode each configuration puts it in, not
    /// from this test. So if the sender's lanes ever change tags, this follows them. Every lane that can be
    /// live in that mode sends one block before the writer is allowed to run; the file must then hold ONE
    /// block of all of them mixed. Two lanes sharing a ring come out as blocks one after another: more
    /// frames than were sent in the time, at the wrong level.</para>
    ///
    /// <para>Sender-side, so the mode is decided by whether an ASIO driver is chosen: WASAPI-only has none,
    /// and ASIO-only and both-lanes share the mode with a driver. All three are driven.</para>
    /// </summary>
    private static string? RecordingKeepsEverySendingLaneApart()
    {
        var temp = Path.Combine(Path.GetTempPath(), "remsound-sendlanes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var findings = new List<string>();
            float[] levels = [0.1f, 0.2f, 0.4f];
            foreach (var configuration in AudioConfigurations.All)
            {
                using var sender = new RemSound.Sender.AudioSender();
                var withDriver = configuration.UsesAsio();
                sender.SetAudioMode(withDriver ? AudioMode.BothIndependent : AudioMode.WasapiOnly,
                    withDriver ? "RemSound self-test - no such ASIO driver" : null);

                // The lanes that can send at the same moment in this mode: the capture lane and the plugin lane
                // always, and the ASIO capture lane only when a driver is chosen.
                var live = new List<RenderRoute> { sender.DefaultLaneRouteForTest, sender.PluginLaneRouteForTest };
                if (withDriver) live.Add(sender.AsioLaneRouteForTest);
                Check(live.Distinct().Count() == live.Count,
                    $"{configuration.Describe()}: the sender's live lanes must hold different route tags ({string.Join(", ", live)}) — "
                    + "the recorder can only keep apart lanes the sender keeps apart");

                var path = Path.Combine(temp, $"{configuration}.wav");
                var settings = new RecordingSettings
                {
                    Source = RecordingSource.SentOnly,
                    FileFormat = RecordingFileFormat.Wav,
                    WavBitsPerSample = 16,
                    ChannelMode = RecordingChannelMode.Stereo,
                };
                var recorder = new AudioRecorder(settings, null, null, path) { HoldWriterForTest = true };
                using (recorder)
                {
                    for (var i = 0; i < live.Count; i++)
                    {
                        var block = new float[480 * 2];
                        Array.Fill(block, levels[i]);
                        recorder.WriteSent(block, live[i]);
                    }
                    recorder.Stop();
                }

                var frames = (new FileInfo(path).Length - 44) / 4;   // 16-bit stereo after a 44-byte header
                var expected = levels.Take(live.Count).Sum();
                var peak = PeakOfWav(path);
                Check(frames == 480,
                    $"{configuration.Describe()}: {live.Count} lanes each sending 10 ms at the same moment must record 10 ms of "
                    + $"them MIXED — the file holds {frames} frames. More means two lanes shared one ring and landed one after "
                    + "the other, which is what two audio threads writing the same ring looks like on disk");
                Check(Math.Abs(peak - expected) < 0.02f,
                    $"{configuration.Describe()}: the mixed block must carry every lane at once (peak {peak:0.000}, expected "
                    + $"{expected:0.000})");
                findings.Add($"{configuration.Describe()}: {live.Count} lanes mixed into one block at {peak:0.00}");
            }
            return string.Join("; ", findings);
        }
        finally { try { Directory.Delete(temp, recursive: true); } catch { } }
    }
}
