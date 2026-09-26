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

    /// <summary>A RECORDING THAT DIES MUST SAY SO.
    ///
    /// <para>Found in the 2026-08-22 audit. If the writer thread threw — a full disk, a network drive
    /// going away, permissions changing under it — it logged one line and exited. Nothing else
    /// noticed. The recorder object still existed, so the app went on reporting that it was recording,
    /// the menu went on offering Stop, and every frame from that moment was dropped into a queue
    /// nobody was draining. An hour of recording, a file with the first few seconds in it, and you
    /// find out when you open it.</para>
    ///
    /// <para>The failure itself is real life and cannot be prevented from inside the app. Failing
    /// SILENTLY is the part that had to go.</para></summary>
    private static string? RecordingReportsItsOwnDeath()
    {
        var temp = Path.Combine(Path.GetTempPath(), "remsound-recfail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        using var receiver = new AudioReceiver();
        using var sender = new RemSound.Sender.AudioSender();
        try
        {
            string? failureReason = null;
            string? failedPath = null;
            var controller = new RecordingController(sender, receiver, new RemSoundSettingsStore("RemSound"), _ => { })
            {
                SettingsSourceForTest = () => new RecordingSettings
                {
                    FileFormat = RecordingFileFormat.Wav,
                    Source = RecordingSource.SentOnly,
                    Folder = temp,
                },
            };
            controller.RecordingFailed += (reason, path) => { failureReason = reason; failedPath = path; };

            controller.Start();
            Check(controller.IsRecording, "the recording must be running before it can be killed");

            // Arm the failure, then feed audio so the writer wakes up and hits it.
            controller.FailNextWriteForTest();
            var block = MakeBlock(0.4f);
            for (var i = 0; i < 40 && failureReason is null; i++)
            {
                sender.OnSentSamples?.Invoke(block.AsMemory(), RenderRoute.Mixed);
                Thread.Sleep(25);
            }

            Check(failureReason is not null,
                "a writer that dies must REPORT it - this is the whole finding: it used to log one line and let the "
              + "app go on claiming to record into a file that had stopped growing");
            Check(failureReason!.Contains("simulated disk failure"),
                $"the report must carry the real reason so the user is told what happened (got: {failureReason})");
            Check(failedPath is not null, "...and which file was partly written, so whatever was salvaged can be found");

            controller.Stop();
            Check(!controller.IsRecording, "and stopping afterwards must leave the app honest about its state");

            // Only once, however many tracks fall over - asked of a SPLIT recording, where it matters: one writer per
            // person plus your own, all falling over together (a full disk takes every track at once). This used to
            // re-arm the single-file writer that had already died, which can never report twice (found 2026-09-24).
            var splitFolder = Path.Combine(temp, "split");
            Directory.CreateDirectory(splitFolder);
            var andre = new IPEndPoint(IPAddress.Parse("192.168.1.61"), 47830);
            var chris = new IPEndPoint(IPAddress.Parse("192.168.1.62"), 47830);
            var split = new RecordingController(sender, receiver, new RemSoundSettingsStore("RemSound"), _ => { })
            {
                SettingsSourceForTest = () => new RecordingSettings
                {
                    SplitTracks = true,
                    Source = RecordingSource.Both,
                    FileFormat = RecordingFileFormat.Wav,
                    Folder = splitFolder,
                },
                ConnectedPeersProvider = () => new[] { (andre.Address, "Andre"), (chris.Address, "Chris") },
            };
            var splitReports = 0;
            string? splitReason = null;
            split.RecordingFailed += (reason, _) => { Interlocked.Increment(ref splitReports); splitReason = reason; };
            split.Start();
            Check(split.IsRecording, "the split recording must be running before it can be killed");
            split.FailNextWriteForTest();
            var peerTap = receiver.PeerRecordTapForTest;
            var blockDone = receiver.OnRecordBlockComplete;
            for (var i = 0; i < 80 && split.FaultedRecordersForTest < 3; i++)
            {
                peerTap?.Invoke(andre, block.AsMemory());
                peerTap?.Invoke(chris, block.AsMemory());
                blockDone?.Invoke(block.Length);
                sender.OnSentSamples?.Invoke(block.AsMemory(), RenderRoute.Mixed);
                Thread.Sleep(10);
            }
            var died = split.FaultedRecordersForTest;
            Thread.Sleep(100);   // a late second report lands before the count is read
            Check(died >= 2, $"the split half must have more than one writer fall over, or it asks nothing (only {died} died)");
            Check(splitReports == 1,
                $"a split recording whose {died} tracks all fell over must tell the person ONCE, not once per track (told {splitReports} times)");
            Check(splitReason?.Contains("simulated disk failure") == true, $"and with the real reason (got: {splitReason})");
            split.Stop();
            Check(!split.IsRecording, "and stopping the split recording afterwards must leave the app honest about its state");

            return "a dying writer reports itself with the reason and the partial file, exactly once per recording - "
                 + $"including a split recording where all {died} tracks died at once";
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
            // Judged by what the files HOLD, not their size: a kept side written as silence of the right length passed the
            // size checks (found 2026-09-24).
            CheckHoldsTheTestTone(Path.Combine(temp, "received.wav"), "a ReceivedOnly recording fed received audio");
            Check(RmsOfRecording(Path.Combine(temp, "received-none.wav")) < 0.01,
                $"a ReceivedOnly recorder fed ONLY your own send must hold silence ({received} vs {receivedNone} bytes; RMS {RmsOfRecording(Path.Combine(temp, "received-none.wav")):0.000})");
            findings.Add("ReceivedOnly keeps received and drops sent");

            var sent = RecordTone(temp, Path.Combine(temp, "sent.wav"),
                new RecordingSettings { FileFormat = RecordingFileFormat.Wav, Source = RecordingSource.SentOnly },
                feedReceived: true, feedSent: true);
            var sentNone = RecordTone(temp, Path.Combine(temp, "sent-none.wav"),
                new RecordingSettings { FileFormat = RecordingFileFormat.Wav, Source = RecordingSource.SentOnly },
                feedReceived: true, feedSent: false);
            CheckHoldsTheTestTone(Path.Combine(temp, "sent.wav"), "a SentOnly recording fed your send");
            Check(RmsOfRecording(Path.Combine(temp, "sent-none.wav")) < 0.01,
                $"a SentOnly recorder fed ONLY received audio must hold silence ({sent} vs {sentNone} bytes; RMS {RmsOfRecording(Path.Combine(temp, "sent-none.wav")):0.000})");
            findings.Add("SentOnly keeps sent and drops received");

            var both = RecordTone(temp, Path.Combine(temp, "both.wav"),
                new RecordingSettings { FileFormat = RecordingFileFormat.Wav, Source = RecordingSource.Both },
                feedReceived: true, feedSent: true);
            CheckHoldsTheTestTone(Path.Combine(temp, "both.wav"), "a Both recording of the exchange");
            // Both must carry EITHER side on its own - a mix that silently needs both present would
            // record nothing during the stretches when only one person is talking.
            var bothReceivedOnly = RecordTone(temp, Path.Combine(temp, "both-r.wav"),
                new RecordingSettings { FileFormat = RecordingFileFormat.Wav, Source = RecordingSource.Both },
                feedReceived: true, feedSent: false);
            var bothSentOnly = RecordTone(temp, Path.Combine(temp, "both-s.wav"),
                new RecordingSettings { FileFormat = RecordingFileFormat.Wav, Source = RecordingSource.Both },
                feedReceived: false, feedSent: true);
            CheckHoldsTheTestTone(Path.Combine(temp, "both-r.wav"), $"Both, with only received audio ({bothReceivedOnly}B)");
            CheckHoldsTheTestTone(Path.Combine(temp, "both-s.wav"), $"Both, with only your send ({bothSentOnly}B)");
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
                foreach (var track in Directory.GetFiles(Path.Combine(temp, "split-" + ext), "*." + ext, SearchOption.AllDirectories))
                    Check(RmsOfRecording(track) > 0.05,
                        $"every split track in {fmt} must hold the audio fed to it, not just be bigger than 200 bytes ({Path.GetFileName(track)}: RMS {RmsOfRecording(track):0.000})");
            }
            // EVERY extension, not only the surprising one. The loop above asks the recorder what
            // extension it uses and then looks for exactly that, so it stays self-consistent whatever
            // the answer is — changing FLAC to write ".wav" left the gate green (2026-08-24). The
            // extension is what decides whether the file Ed is handed opens in his tools at all.
            Check(AudioRecorder.ExtensionFor(RecordingFileFormat.Wav) == "wav", "WAV recordings are .wav");
            Check(AudioRecorder.ExtensionFor(RecordingFileFormat.Mp3) == "mp3", "MP3 recordings are .mp3");
            Check(AudioRecorder.ExtensionFor(RecordingFileFormat.Flac) == "flac",
                $"FLAC recordings are .flac — a FLAC stream in a file called something else is one most players refuse to "
                + $"open (got .{AudioRecorder.ExtensionFor(RecordingFileFormat.Flac)})");
            Check(AudioRecorder.ExtensionFor(RecordingFileFormat.Ogg) == "opus",
                "OGG-Opus recordings are written as .opus - pinned because a test that assumed .ogg silently found no files");
            Check(new[] { RecordingFileFormat.Wav, RecordingFileFormat.Mp3, RecordingFileFormat.Flac, RecordingFileFormat.Ogg }
                    .Select(AudioRecorder.ExtensionFor).Distinct().Count() == 4,
                "each recording format must have its OWN extension, or two of them produce files nothing can tell apart");
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

    /// <summary>
    /// AUDIT REC1: a peer arriving on BOTH lanes in one render must keep all of both blocks.
    ///
    /// <para>The per-peer record tap sums each lane's block into that peer's accumulator, so a peer
    /// heard on the WASAPI lane and the ASIO lane is recorded as it sounds rather than twice in a
    /// row. The two lanes have their own device periods, so their blocks are routinely different
    /// LENGTHS — and the accumulator recorded the length of the block it saw LAST, not the longest.
    /// The flush then padded "from Len to the render length" with silence, which zeroed the tail of
    /// the longer lane's audio that had already been summed in.</para>
    ///
    /// <para>Same shape twice: the resize was <c>new float[]</c> rather than <c>Array.Resize</c>, so
    /// a short block followed by a longer one threw the short block's audio away entirely. The flush
    /// three methods down already used Array.Resize, which is what makes this an oversight rather
    /// than a decision.</para>
    ///
    /// <para>Constant-amplitude blocks, not the sine the other recording tests use: a sine crosses
    /// zero on its own, and this has to be able to tell "the recorder wrote silence" from "the tone
    /// happened to be near zero". 2026-08-24.</para>
    /// </summary>
    private static string? AuditPeerTrackKeepsBothLanes()
    {
        var temp = Path.Combine(Path.GetTempPath(), "remsound-rec1-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            using var receiver = new AudioReceiver();
            using var sender = new RemSound.Sender.AudioSender();
            var peer = new IPEndPoint(IPAddress.Parse("192.168.1.61"), 47830);
            var controller = new RecordingController(sender, receiver, new RemSoundSettingsStore("RemSound"), _ => { })
            {
                SettingsSourceForTest = () => new RecordingSettings
                {
                    SplitTracks = true,
                    Source = RecordingSource.ReceivedOnly,
                    FileFormat = RecordingFileFormat.Wav,
                    Folder = temp,
                },
                ConnectedPeersProvider = () => new[] { (peer.Address, "Twolanes") },
            };
            controller.Start();

            var tap = Require(receiver.PeerRecordTapForTest, "the per-peer record tap must be wired by Start");
            var blockDone = Require(receiver.OnRecordBlockComplete, "the render-complete hook must be wired by Start");

            // One render, twice over: the LONG lane first and the SHORT lane second, then the other
            // way round. Both orders matter — one exercises the truncating length, the other the
            // discarding resize.
            const int LongFrames = 480, ShortFrames = 240;
            var longBlock = MakeConstantBlock(0.4f, LongFrames);
            var shortBlock = MakeConstantBlock(0.4f, ShortFrames);
            for (var i = 0; i < 60; i++)
            {
                if (i % 2 == 0) { tap(peer, longBlock.AsMemory()); tap(peer, shortBlock.AsMemory()); }
                else { tap(peer, shortBlock.AsMemory()); tap(peer, longBlock.AsMemory()); }
                blockDone(longBlock.Length);
                Thread.Sleep(2);
            }
            controller.Stop();
            Thread.Sleep(400);

            // Split recordings land in a dated "multi track" subfolder, so search the tree.
            var file = Directory.GetFiles(temp, "*.wav", SearchOption.AllDirectories)
                .OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault();
            file = Require(file, "the peer's track must have been written");
            var silent = SilentFractionOfWav(file);
            Check(silent < 0.05,
                $"{silent:P0} of the peer's track is silence. A peer heard on two lanes in one render must keep BOTH "
                + $"blocks: recording the LAST block's length instead of the longest makes the flush pad over audio it "
                + $"had already summed in, so the tail of the longer lane is overwritten with silence — and a recording "
                + $"that loses half of somebody is found out when the file is opened, not while it is being made");
            return $"a peer on two lanes of different block lengths keeps both, either order round ({silent:P1} silence)";
        }
        finally { try { Directory.Delete(temp, recursive: true); } catch { } }
    }

    /// <summary>
    /// AUDIT REC2: a WAV that reaches the 4 GB the format can describe must STOP and say so.
    ///
    /// <para>The RIFF and data-chunk size fields are unsigned 32-bit, and the writer truncated a long
    /// into them silently. Past 4 GB the file kept growing while its header described a different,
    /// wrong length — so a player reads part of it and stops. At 48 kHz stereo that is about three
    /// hours of 32-bit float or four of 24-bit: inside a long rehearsal, and precisely the failure you
    /// discover when you open the file rather than while you are making it.</para>
    ///
    /// <para>The cap is shrunk for the test — waiting three hours to find out is not a test. What is
    /// asserted is the behaviour that matters: the recording reports its own death with a reason a
    /// person can act on, and the file written up to that point is still valid and still has audio in
    /// it. 2026-08-24.</para>
    /// </summary>
    private static string? AuditWavStopsAtTheFormatLimit()
    {
        var temp = Path.Combine(Path.GetTempPath(), "remsound-rec2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var restoreCap = AudioRecorder.WavDataCapForTest;
        try
        {
            AudioRecorder.WavDataCapForTest = 256 * 1024;   // a quarter of a megabyte, reached in a moment

            string? reported = null;
            var path = Path.Combine(temp, "capped.wav");
            var settings = new RecordingSettings { FileFormat = RecordingFileFormat.Wav, Source = RecordingSource.ReceivedOnly, Folder = temp };
            using (var recorder = new AudioRecorder(settings, null, null, path))
            {
                recorder.Faulted += reason => reported = reason;
                var block = MakeConstantBlock(0.4f, 480);
                for (var i = 0; i < 400 && reported is null; i++)
                {
                    recorder.WriteReceived(block.AsMemory(), RenderRoute.Mixed);
                    Thread.Sleep(2);
                }
                Thread.Sleep(300);
                reported ??= recorder.FaultReason;
            }

            reported = Require(reported,
                "a WAV that runs past the 4 GB its header can describe must report itself as any other writer death "
                + "does. Truncating the size field silently leaves a growing file whose header describes a different "
                + "length, and the user finds out when they open it");
            Check(reported.Contains("GB", StringComparison.Ordinal) && reported.Contains("hours", StringComparison.Ordinal),
                $"the reason must tell a person what happened and roughly when, not name a field — got: {reported}");
            Check(reported.Contains("FLAC", StringComparison.OrdinalIgnoreCase),
                "and what to do instead — FLAC has no such limit");

            Check(File.Exists(path), "the audio recorded before the limit must still be on disk");
            var peak = PeakOfWav(path);
            Check(peak > 0.3f, $"...and must still be a valid, playable WAV with the audio in it (peak {peak:0.000})");
            return $"a WAV stops at the format's own limit, says so in plain English, and leaves a valid file behind (peak {peak:0.00})";
        }
        finally
        {
            AudioRecorder.WavDataCapForTest = restoreCap;
            try { Directory.Delete(temp, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// AUDIT INST1: the wait-then-delete remover must refuse a folder that isn't an install.
    ///
    /// <para>The uninstaller runs <c>rd /s /q</c> on whatever folder the install marker sits next to,
    /// and the marker is just a file — copy an installed RemSound elsewhere and it goes with it. Copy
    /// one to a drive root and "Uninstall RemSound" would have generated <c>rd /s /q "D:\"</c>.
    /// Unlikely, catastrophic, and free to prevent.</para>
    ///
    /// <para>The real remover is never RUN here, for obvious reasons — the decision is a pure
    /// function so it can be checked without one.</para>
    /// </summary>
    private static string? AuditUninstallerRefusesDangerousFolders()
    {
        foreach (var bad in new[]
                 {
                     null, "", "   ",
                     @"C:\", @"D:\", @"C:", "/",
                     Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                     Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                     Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                 })
        {
            if (bad is not null && bad.Length > 0 && !Path.IsPathRooted(bad)) continue;   // not applicable on this machine
            Check(!AppInstaller.IsSafeToRemove(bad),
                $"the uninstaller must REFUSE \"{bad ?? "(null)"}\" — it deletes the folder recursively, and that one "
                + "holds other people's things");
        }

        // ...and a real install must still be removable, or the guard has broken uninstall instead.
        foreach (var good in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "RemSound"),
                     @"C:\RemSound",
                     @"D:\proj\RemSound\publish",
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "RemSound"),
                 })
        {
            Check(AppInstaller.IsSafeToRemove(good),
                $"a genuine install folder must still be removable — refusing \"{good}\" would break uninstall for "
                + "everybody, which is a worse bug than the one being guarded against");
        }

        // A trailing separator must not change the answer: "D:\" and "D:" are the same place.
        Check(!AppInstaller.IsSafeToRemove(@"D:\\"), "a drive root with a trailing separator is still a drive root");
        Check(AppInstaller.IsSafeToRemove(@"C:\RemSound\"), "a real folder with a trailing separator is still fine");

        // AND that the remover actually ASKS.
        //
        // Everything above proves the RULE. It does not prove the rule is applied — deleting the call
        // site left this step green on the first attempt, which is exactly the outcome-instead-of-
        // mechanism trap. The obvious fix, driving the real StartDeleteAfterExit with a dangerous
        // path, is not on: if the guard were missing, the test would write and run a script that
        // deletes a drive. A test must never be able to destroy the machine to prove a guard exists.
        //
        // So the wiring is checked by reading the source, the same way the gate's other structural
        // guards are. Stated plainly because it is weaker than driving the code: it proves the call
        // is written, not that it runs.
        var root = FindSourceRoot();
        if (root is null)
            return Skip("the rule is proved, but the WIRING could not be checked (no source tree — set REMSOUND_SOURCE_ROOT)");

        var installerSource = File.ReadAllText(Path.Combine(root, "src", "RemSound.App", "AppInstaller.cs"));
        var removerStart = installerSource.IndexOf("private static void StartDeleteAfterExit", StringComparison.Ordinal);
        Check(removerStart >= 0, "StartDeleteAfterExit not found — this wiring check would prove nothing");
        var removerBody = installerSource[removerStart..];
        var guardAt = removerBody.IndexOf("IsSafeToRemove", StringComparison.Ordinal);
        var deleteAt = removerBody.IndexOf("rd /s /q", StringComparison.Ordinal);
        Check(guardAt >= 0,
            "StartDeleteAfterExit must ASK IsSafeToRemove. Without the call the rule above is a function nobody "
            + "invokes, and the remover will delete whatever folder it is handed");
        Check(deleteAt >= 0, "StartDeleteAfterExit no longer builds a delete — this check is watching the wrong method");
        Check(guardAt < deleteAt,
            "the safety check must come BEFORE the delete is composed, not after it");

        return "a drive root, a UNC-style root and every shared system folder are refused; real install folders are "
             + "not; and the remover asks before it composes a delete (wiring checked by source, not driven — a test "
             + "that proves this by running it could delete a drive)";
    }

    /// <summary>
    /// AUDIT SI1: "force close the other copy" must never reach for the Windows service.
    ///
    /// <para>The service runs the same RemSound.exe, so its process is also called "RemSound" — and
    /// the single-instance guard enumerated by name, excluding only its own pid. Choosing "force
    /// close" therefore tried to terminate the service: access denied unelevated, which routed it into
    /// the elevated taskkill and put a UAC prompt in front of somebody who had only asked to close an
    /// app. Accepting it kills the lock-screen service.</para>
    ///
    /// <para>It was never the cause of the dialog either: the single-instance mutex is created without
    /// a Global prefix, so it is per-SESSION, and the service sits in session 0. Nothing outside this
    /// session can be holding the lock, so nothing outside it should ever be killed.</para>
    ///
    /// <para>Checked as a rule AND against the live machine — if a session-0 RemSound is running right
    /// now, the enumeration must not be offering it up. 2026-08-24.</para>
    /// </summary>
    private static string? AuditForceCloseSparesTheService()
    {
        // The rule. Session 0 is where services live; our own session is never 0 for an interactive app.
        Check(!SingleInstanceCoordinator.IsKillableInstance(pid: 7024, selfPid: 100, sessionId: 0, ownSessionId: 1),
            "a process in session 0 is a SERVICE — the per-session lock cannot be held there, so killing it could "
            + "never resolve the conflict, and trying puts a UAC prompt in front of the user");
        Check(!SingleInstanceCoordinator.IsKillableInstance(pid: 200, selfPid: 100, sessionId: 2, ownSessionId: 1),
            "another user's session holds its own lock, not ours — leave it alone");
        Check(!SingleInstanceCoordinator.IsKillableInstance(pid: 100, selfPid: 100, sessionId: 1, ownSessionId: 1),
            "never kill ourselves");
        Check(!SingleInstanceCoordinator.IsKillableInstance(pid: 200, selfPid: 100, sessionId: null, ownSessionId: 1),
            "a session we cannot read means DON'T — never terminate a process on a guess");
        Check(SingleInstanceCoordinator.IsKillableInstance(pid: 200, selfPid: 100, sessionId: 1, ownSessionId: 1),
            "a genuine second copy in our own session must still be closeable, or the force-close option is broken");

        // ...and against whatever is actually running. This is the case that was live on Ed's machine.
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        int ownSession;
        try { ownSession = self.SessionId; }
        catch { return Skip("the rule holds, but this machine's session could not be read, so the live check could not run"); }

        var serviceSeen = 0;
        foreach (var p in System.Diagnostics.Process.GetProcessesByName("RemSound"))
        {
            using (p)
            {
                int session;
                try { session = p.SessionId; } catch { continue; }
                if (session != 0) continue;
                serviceSeen++;
                Check(!SingleInstanceCoordinator.IsKillableInstance(p.Id, self.Id, session, ownSession),
                    $"a session-0 RemSound (pid {p.Id} — the Windows service) is running on this machine and the guard "
                    + "would offer it up to be killed");
            }
        }
        return serviceSeen > 0
            ? $"the rule holds, and the live session-0 service (x{serviceSeen}) on this machine is spared"
            : "the rule holds; no session-0 RemSound is running here, so only the rule could be checked";
    }

    /// <summary>
    /// AUDIT DISC1: a malformed discovery broadcast must be dropped, not crash the app.
    ///
    /// <para>Discovery listens on a broadcast UDP port, so anything on the network can send to it and
    /// a corrupted packet can arrive with nobody meaning harm. The announced AUDIO PORT went from the
    /// wire into <c>PeerAnnouncement</c> unchecked, and from there into <c>new IPEndPoint(address,
    /// port)</c> on the UI thread — which throws outside 0–65535. Nothing wires
    /// <c>Application.ThreadException</c>, so ONE datagram announcing port 99999 took RemSound down.
    /// The port that would have done it is the first case below. 2026-08-24.</para>
    /// </summary>
    private static string? AuditDiscoveryRejectsMalformedAnnouncements()
    {
        var from = IPAddress.Parse("192.168.1.99");
        var mine = Guid.NewGuid();
        static byte[] Json(string s) => System.Text.Encoding.UTF8.GetBytes(s);
        var theirs = Guid.NewGuid();

        bool Parse(string json, out PeerAnnouncement peer) =>
            PeerDiscoveryService.TryParseAnnouncement(Json(json), from, mine, out peer);

        // THE CRASH. An out-of-range port reached new IPEndPoint on the UI thread.
        Check(!Parse($"{{\"InstanceId\":\"{theirs}\",\"Name\":\"evil\",\"AudioPort\":99999,\"CanSend\":true,\"CanReceive\":true}}", out _),
            "a port above 65535 must be REJECTED — it reached new IPEndPoint on the UI thread, and nothing catches a "
            + "throw there, so one broadcast datagram closed RemSound");
        Check(!Parse($"{{\"InstanceId\":\"{theirs}\",\"Name\":\"evil\",\"AudioPort\":-1,\"CanSend\":true,\"CanReceive\":true}}", out _),
            "and a negative port, the same way in");
        Check(!Parse($"{{\"InstanceId\":\"{theirs}\",\"Name\":\"evil\",\"AudioPort\":0,\"CanSend\":true,\"CanReceive\":true}}", out _),
            "port 0 names no service — a peer that cannot say where to reach it is not a peer");

        // Everything else that can arrive on an open UDP port.
        Check(!Parse("not json at all", out _), "garbage must be dropped, not thrown on");
        Check(!Parse("", out _), "an empty datagram must be dropped");
        Check(!Parse("{}", out _), "an announcement with no fields must be dropped");
        Check(!PeerDiscoveryService.TryParseAnnouncement([], from, mine, out _), "a zero-length payload must be dropped");
        Check(!Parse($"{{\"InstanceId\":\"{Guid.Empty}\",\"AudioPort\":47830}}", out _),
            "an all-zero instance id must be dropped — it would collide with itself for every sender using it");
        Check(!Parse($"{{\"InstanceId\":\"{mine}\",\"AudioPort\":47830}}", out _),
            "our OWN announcement coming back off the broadcast must be ignored");

        // A real one still gets through, or discovery is simply broken.
        Check(Parse($"{{\"InstanceId\":\"{theirs}\",\"Name\":\"Andre\",\"AudioPort\":47830,\"CanSend\":true,\"CanReceive\":true}}", out var good),
            "a genuine announcement must still be accepted");
        Check(good.AudioPort == 47830 && good.Name == "Andre" && Equals(good.Address, from),
            $"...and carry its own details (got port {good.AudioPort}, name \"{good.Name}\", from {good.Address})");
        Check(Parse($"{{\"InstanceId\":\"{theirs}\",\"Name\":\"   \",\"AudioPort\":47830}}", out var unnamed)
              && unnamed.Name == from.ToString(),
            "a blank name must fall back to the address rather than showing an empty row");

        // A name long enough to bury a log line or a screen reader is trimmed, not taken whole.
        var huge = new string('x', 5000);
        Check(Parse($"{{\"InstanceId\":\"{theirs}\",\"Name\":\"{huge}\",\"AudioPort\":47830}}", out var longName)
              && longName.Name.Length <= 128,
            $"an absurd peer name must be trimmed — it is shown in lists, spoken aloud and written into every log "
            + $"line that mentions the peer (got {longName.Name.Length} characters)");

        return "out-of-range ports, garbage, empty datagrams, a zero id and our own echo are all dropped; a genuine "
             + "announcement still arrives intact and an absurd name is trimmed";
    }

    private static float[] MakeConstantBlock(float amplitude, int frames)
    {
        var block = new float[frames * 2];
        for (var i = 0; i < block.Length; i++) block[i] = amplitude;
        return block;
    }

    /// <summary>What fraction of a float WAV is at (or almost at) zero. Peak cannot answer this: a
    /// recording that lost half its samples still peaks at full level on the half that survived.</summary>
    private static double SilentFractionOfWav(string path)
    {
        using var reader = new NAudio.Wave.AudioFileReader(path);
        var buffer = new float[4096];
        long zero = 0, total = 0;
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
            for (var i = 0; i < read; i++) { total++; if (Math.Abs(buffer[i]) < 0.01f) zero++; }
        return total == 0 ? 1.0 : zero / (double)total;
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

    /// <summary>
    /// RECORDING MUST WORK IN ALL THREE AUDIO CONFIGURATIONS — including ASIO-only.
    ///
    /// <para>Every other recording test in this file runs in one configuration and never says which.
    /// The per-peer record tap rides on the SESSION, and a session is only read when the lane it
    /// landed on is actually rendered — so "does recording capture anything on an ASIO-only rig"
    /// was a question nothing here asked. Structurally it should work; that is not the same as
    /// knowing it does, and the difference is a rehearsal recorded as silence.</para>
    ///
    /// <para>Each configuration is rendered through the SAME surfaces CompositeRenderBackend uses for
    /// it: the all-sessions read where no ASIO driver is chosen, and each lane's own filtered surface
    /// otherwise. Reading a surface the app would not read would prove nothing about the app.</para>
    ///
    /// <para>Written 2026-08-24 while auditing the EXISTING tests against the three configurations,
    /// at Ed's insistence — the guard only inspects tests that MENTION a lane, so a recording test
    /// that never says "ASIO" was invisible to it and had never been asked the question.</para>
    /// </summary>
    private static string? RecordingCapturesInEveryConfiguration()
    {
        var peer = new IPEndPoint(IPAddress.Parse("192.168.1.62"), 47830);
        var summary = new List<string>();

        foreach (var configuration in AudioConfigurations.All)
        {
            var engine = new PlayoutEngine(new ReceiverDiagnostics());
            engine.SetIndependentLaneLatency(configuration.HasTwoLanes());
            engine.SetLaneActive(RenderRoute.WasapiLane, configuration.UsesWasapi());
            engine.SetLaneActive(RenderRoute.AsioLane, configuration.UsesAsio());
            engine.SetMaxLatencyMs(RenderRoute.Mixed, 30);
            engine.SetMaxLatencyMs(RenderRoute.WasapiLane, 30);
            engine.SetMaxLatencyMs(RenderRoute.AsioLane, 30);

            var framesTapped = 0;
            var peak = 0f;
            engine.SetRecordTap((_, block) =>
            {
                framesTapped += block.Length;
                foreach (var sample in block.Span) peak = Math.Max(peak, Math.Abs(sample));
            }, raw: false);

            var session = engine.GetOrCreateSession(peer, 1, 1024 * 1024);
            var expectedRoute = configuration.SingleRoute() ?? RenderRoute.WasapiLane;
            Check(session.Route == expectedRoute,
                $"in {configuration.Describe()} the peer must land on {expectedRoute} (got {session.Route}) — a stream on a "
                + "lane nobody renders is never read, so it is never recorded either");
            FillSession(session, 0.5f);

            // The very surfaces CompositeRenderBackend renders this configuration from: one
            // all-sessions read with no ASIO driver chosen, otherwise each lane's filtered surface.
            var surfaces = configuration switch
            {
                AudioConfiguration.WasapiOnly => new NAudio.Wave.IWaveProvider[] { engine },
                AudioConfiguration.AsioOnly => [engine.AsioLaneOutput],
                _ => [engine.WasapiLaneOutput, engine.AsioLaneOutput],
            };

            var buffer = new byte[960 * 8];
            for (var i = 0; i < 12; i++)
            {
                foreach (var surface in surfaces)
                {
                    Array.Clear(buffer);
                    surface.Read(buffer, 0, buffer.Length);
                }
            }

            Check(framesTapped > 0,
                $"in {configuration.Describe()} the recorder's per-peer tap never fired — nothing would be written to the "
                + "track at all. The tap rides on the session, so a configuration whose lane is never rendered records "
                + "silence, and that is discovered when the file is opened rather than while it is being made");
            Check(peak > 0.05f,
                $"in {configuration.Describe()} the tap fired but carried only {peak:0.000} peak — a track of silence is "
                + "the same loss as no track, and it passes any check that only asks whether a file exists");
            summary.Add($"{configuration.Describe()}: {framesTapped} samples, peak {peak:0.00}");
        }

        return "a peer's audio reaches the recorder in all three configurations — " + string.Join("; ", summary);
    }
}
