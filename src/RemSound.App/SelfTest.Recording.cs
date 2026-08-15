using System.Net;
using RemSound.Core;
using RemSound.Receiver;

namespace RemSound.App;

/// <summary>
/// RECORDING — every mode, and multi-peer split tracks with real content in each file.
///
/// <para>Ed, 2026-08-15: "have you built tests for every single mode of recording including multi peer
/// recording?" The honest answer was no. The formats were checked only for "the file is bigger than
/// 200 bytes", which a file of pure silence passes. The split-track test used ONE peer and only ever
/// fed your own send, so the per-peer files were created and never written to — the exact thing the
/// feature exists for was untested.</para>
///
/// <para><b>So each peer gets DIFFERENT audio here, and each file is checked for the right one.</b>
/// Two peers at two levels: if the routing crossed over, or every track got the mix, or a peer's
/// buffer went to the wrong file, the files would still all exist and still all be the right size.
/// Only the contents tell them apart.</para>
///
/// <para>Files are verified by reading them back, not by their length. A silent WAV of the right
/// duration is the most convincing wrong answer this feature can give.</para>
/// </summary>
internal static partial class SelfTest
{
    private static string? RecordingMultiPeerSplit()
    {
        var temp = Path.Combine(Path.GetTempPath(), "remsound-multipeer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        using var receiver = new AudioReceiver();
        using var sender = new RemSound.Sender.AudioSender();
        try
        {
            var andre = new IPEndPoint(IPAddress.Parse("192.168.1.50"), 47830);
            var chris = new IPEndPoint(IPAddress.Parse("192.168.1.51"), 47830);

            var controller = new RecordingController(sender, receiver, new RemSoundSettingsStore("RemSound"), _ => { })
            {
                SettingsSourceForTest = () => new RecordingSettings
                {
                    SplitTracks = true,
                    Source = RecordingSource.Both,
                    FileFormat = RecordingFileFormat.Wav,
                    Folder = temp,
                },
                ConnectedPeersProvider = () => new[] { (andre.Address, "Andre"), (chris.Address, "Chris") },
            };

            controller.Start();
            Check(controller.IsRecording, "split recording must be running after Start");

            // Two peers, DELIBERATELY different levels. Same level in both files would pass even if
            // the routing had crossed over, and crossed routing is the failure this feature invites.
            var peerTap = receiver.PeerRecordTapForTest;
            var blockDone = receiver.OnRecordBlockComplete;
            Check(peerTap is not null, "split recording must install a per-peer tap on the receiver");
            Check(blockDone is not null, "...and a per-block flush, or the peer tracks never reach disk");

            var loud = MakeBlock(0.5f);
            var quiet = MakeBlock(0.1f);
            var sent = MakeBlock(0.3f);
            var sendTap = sender.OnSentSamples;
            Check(sendTap is not null, "your own send track must be tapped too");

            for (var i = 0; i < 120; i++)
            {
                peerTap!(andre, loud.AsMemory());
                peerTap(chris, quiet.AsMemory());
                blockDone!(loud.Length);
                sendTap!(sent.AsMemory(), RenderRoute.Mixed);
                Thread.Sleep(1);
            }

            controller.Stop();
            for (var i = 0; i < 60 && Directory.GetFiles(temp, "*.wav", SearchOption.AllDirectories).Length < 3; i++) Thread.Sleep(25);

            var files = Directory.GetFiles(temp, "*.wav", SearchOption.AllDirectories);
            Check(files.Length >= 3, $"two peers plus your own send must make three files (found {files.Length}: {string.Join(", ", files.Select(Path.GetFileName))})");

            var andreFile = files.FirstOrDefault(f => Path.GetFileName(f).StartsWith("Andre", StringComparison.OrdinalIgnoreCase));
            var chrisFile = files.FirstOrDefault(f => Path.GetFileName(f).StartsWith("Chris", StringComparison.OrdinalIgnoreCase));
            Check(andreFile is not null, "a peer's track must be NAMED after them, not after their address");
            Check(chrisFile is not null, "...both of them");

            var andrePeak = PeakOfWav(andreFile!);
            var chrisPeak = PeakOfWav(chrisFile!);

            // THE test the old one never made: each file holds that peer's own audio.
            Check(andrePeak > 0.3f, $"Andre's track must contain Andre's audio, not silence (peak {andrePeak:0.000})");
            Check(chrisPeak > 0.05f, $"Chris's track must contain Chris's audio (peak {chrisPeak:0.000})");
            Check(andrePeak > chrisPeak * 2,
                $"each track must hold ITS OWN peer - Andre was fed five times Chris's level, so the files must differ in the same way "
              + $"(Andre {andrePeak:0.000}, Chris {chrisPeak:0.000}). Equal levels here would mean the routing crossed or every track got the mix");

            var meFile = files.First(f => f != andreFile && f != chrisFile);
            var mePeak = PeakOfWav(meFile);
            Check(mePeak > 0.15f, $"your own track must contain what you sent (peak {mePeak:0.000})");
            Check(Math.Abs(mePeak - 0.3f) < 0.1f, $"...at the level you sent it, not somebody else's (peak {mePeak:0.000}, fed 0.300)");

            return $"three files from two peers plus your own send; each carries its OWN audio "
                 + $"(Andre {andrePeak:0.00}, Chris {chrisPeak:0.00}, yours {mePeak:0.00}) and peers are named, not numbered";
        }
        finally { try { Directory.Delete(temp, recursive: true); } catch { } }
    }

    /// <summary>Every source mode and every channel mode, checked by what ends up in the file.
    ///
    /// <para>The old test covered SentOnly only, and judged mono by file size. A downmix that dropped
    /// a channel instead of summing it, or a source gate that let the wrong audio through, produces a
    /// perfectly normal-looking file.</para></summary>
    private static string? RecordingModesByContent()
    {
        var temp = Path.Combine(Path.GetTempPath(), "remsound-recmodes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var findings = new List<string>();

            // --- The three source modes, each fed BOTH kinds of audio ------------------------------
            // Fed both every time, so a gate that ignores its setting is caught rather than flattered
            // by only ever being handed what it wanted.
            var received = RecordTone(temp, Path.Combine(temp, "received.wav"),
                new RecordingSettings { FileFormat = RecordingFileFormat.Wav, Source = RecordingSource.ReceivedOnly },
                feedReceived: true, feedSent: true);
            var receivedNone = RecordTone(temp, Path.Combine(temp, "received-none.wav"),
                new RecordingSettings { FileFormat = RecordingFileFormat.Wav, Source = RecordingSource.ReceivedOnly },
                feedReceived: false, feedSent: true);
            Check(received > 200, $"a ReceivedOnly recorder fed received audio must capture it ({received} bytes)");
            Check(receivedNone < received / 2,
                $"a ReceivedOnly recorder fed ONLY your own send must stay near empty ({receivedNone} bytes against {received})");
            findings.Add("ReceivedOnly keeps received and drops sent");

            var sent = RecordTone(temp, Path.Combine(temp, "sent.wav"),
                new RecordingSettings { FileFormat = RecordingFileFormat.Wav, Source = RecordingSource.SentOnly },
                feedReceived: true, feedSent: true);
            var sentNone = RecordTone(temp, Path.Combine(temp, "sent-none.wav"),
                new RecordingSettings { FileFormat = RecordingFileFormat.Wav, Source = RecordingSource.SentOnly },
                feedReceived: true, feedSent: false);
            Check(sent > 200, $"a SentOnly recorder fed your send must capture it ({sent} bytes)");
            Check(sentNone < sent / 2, $"a SentOnly recorder fed ONLY received audio must stay near empty ({sentNone} bytes against {sent})");
            findings.Add("SentOnly keeps sent and drops received");

            var both = RecordTone(temp, Path.Combine(temp, "both.wav"),
                new RecordingSettings { FileFormat = RecordingFileFormat.Wav, Source = RecordingSource.Both },
                feedReceived: true, feedSent: true);
            Check(both > 200, $"a Both recorder must capture the exchange ({both} bytes)");
            // Both must carry EITHER side on its own - a mix that silently needs both present would
            // record nothing during the stretches when only one person is talking.
            var bothReceivedOnly = RecordTone(temp, Path.Combine(temp, "both-r.wav"),
                new RecordingSettings { FileFormat = RecordingFileFormat.Wav, Source = RecordingSource.Both },
                feedReceived: true, feedSent: false);
            var bothSentOnly = RecordTone(temp, Path.Combine(temp, "both-s.wav"),
                new RecordingSettings { FileFormat = RecordingFileFormat.Wav, Source = RecordingSource.Both },
                feedReceived: false, feedSent: true);
            Check(bothReceivedOnly > 200 && bothSentOnly > 200,
                $"Both must record either side alone, not only when they overlap (received-only {bothReceivedOnly}B, sent-only {bothSentOnly}B)");
            findings.Add("Both takes either side alone");

            // --- Channel modes, checked in the WAV header rather than by size ------------------------
            var stereoPath = Path.Combine(temp, "stereo.wav");
            RecordTone(temp, stereoPath, new RecordingSettings { FileFormat = RecordingFileFormat.Wav, Source = RecordingSource.Both, ChannelMode = RecordingChannelMode.Stereo },
                feedReceived: true, feedSent: false);
            var monoPath = Path.Combine(temp, "mono2.wav");
            RecordTone(temp, monoPath, new RecordingSettings { FileFormat = RecordingFileFormat.Wav, Source = RecordingSource.Both, ChannelMode = RecordingChannelMode.Mono },
                feedReceived: true, feedSent: false);

            Check(WavChannels(stereoPath) == 2, $"a stereo recording must declare two channels (got {WavChannels(stereoPath)})");
            Check(WavChannels(monoPath) == 1, $"a mono recording must declare ONE channel, not a stereo file that happens to be smaller (got {WavChannels(monoPath)})");
            Check(PeakOfWav(monoPath) > 0.05f, $"...and must still contain audio after the downmix (peak {PeakOfWav(monoPath):0.000})");
            findings.Add("mono really is one channel and keeps its audio");

            // --- Every format, in SPLIT mode too. The old coverage tested formats only as a single
            // file; the split path builds its recorders separately and could ship a format that works
            // one way and not the other.
            foreach (var fmt in new[] { RecordingFileFormat.Wav, RecordingFileFormat.Mp3, RecordingFileFormat.Ogg, RecordingFileFormat.Flac })
            {
                // The extension comes from the app, not from this test. The single-file format test
                // hands the recorder a full path, so it never exercises ExtensionFor at all - and the
                // OGG format is written as ".opus", which is exactly the sort of thing a test that
                // guesses the name gets wrong.
                var ext = AudioRecorder.ExtensionFor(fmt);
                var made = SplitRecordCount(temp, fmt, ext);
                Check(made >= 2, $"split recording in {fmt} must produce a track per peer plus your own, named .{ext} (got {made})");
            }
            Check(AudioRecorder.ExtensionFor(RecordingFileFormat.Ogg) == "opus",
                "OGG-Opus recordings are written as .opus - pinned because a test that assumed .ogg silently found no files");
            findings.Add("all four formats work in split mode as well as single-file, with the app's own extensions");

            return string.Join("; ", findings);
        }
        finally { try { Directory.Delete(temp, recursive: true); } catch { } }
    }

    /// <summary>Run a short split recording in one format and report how many files came out.</summary>
    private static int SplitRecordCount(string root, RecordingFileFormat format, string ext)
    {
        var folder = Path.Combine(root, "split-" + ext);
        Directory.CreateDirectory(folder);
        using var receiver = new AudioReceiver();
        using var sender = new RemSound.Sender.AudioSender();
        var peer = new IPEndPoint(IPAddress.Parse("192.168.1.60"), 47830);
        var controller = new RecordingController(sender, receiver, new RemSoundSettingsStore("RemSound"), _ => { })
        {
            SettingsSourceForTest = () => new RecordingSettings
            {
                SplitTracks = true,
                Source = RecordingSource.Both,
                FileFormat = format,
                Folder = folder,
            },
            ConnectedPeersProvider = () => new[] { (peer.Address, "Solo") },
        };
        controller.Start();
        var block = MakeBlock(0.4f);
        var peerTap = receiver.PeerRecordTapForTest;
        var blockDone = receiver.OnRecordBlockComplete;
        var sendTap = sender.OnSentSamples;
        for (var i = 0; i < 80; i++)
        {
            peerTap?.Invoke(peer, block.AsMemory());
            blockDone?.Invoke(block.Length);
            sendTap?.Invoke(block.AsMemory(), RenderRoute.Mixed);
            Thread.Sleep(1);
        }
        controller.Stop();
        // AllDirectories: the controller may file a session into a dated subfolder, and a
        // top-level-only search reported zero for a recording that had worked perfectly.
        for (var i = 0; i < 60 && Directory.GetFiles(folder, "*." + ext, SearchOption.AllDirectories).Length < 2; i++) Thread.Sleep(25);
        return Directory.GetFiles(folder, "*." + ext, SearchOption.AllDirectories).Count(f => new FileInfo(f).Length > 200);
    }

    /// <summary>One render block of a steady stereo tone at the given level.</summary>
    private static float[] MakeBlock(float amplitude)
    {
        var block = new float[480 * 2];
        var phase = 0.0;
        for (var i = 0; i < block.Length; i += 2)
        {
            var v = (float)(amplitude * Math.Sin(phase));
            phase += 2 * Math.PI * 440 / 48000;
            block[i] = v;
            block[i + 1] = v;
        }
        return block;
    }

    /// <summary>Loudest sample in a 32-bit float WAV. Reading the audio back is the only way to tell a
    /// working recording from a correctly-sized silent one.</summary>
    private static float PeakOfWav(string path)
    {
        using var reader = new NAudio.Wave.AudioFileReader(path);
        var buffer = new float[4096];
        var peak = 0f;
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
            for (var i = 0; i < read; i++) peak = Math.Max(peak, Math.Abs(buffer[i]));
        return peak;
    }

    private static int WavChannels(string path)
    {
        using var reader = new NAudio.Wave.WaveFileReader(path);
        return reader.WaveFormat.Channels;
    }
}
