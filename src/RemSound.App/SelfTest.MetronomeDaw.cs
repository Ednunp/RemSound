using System.Diagnostics;
using System.Net;
using NAudio.Wave;
using RemSound.Core;
using RemSound.Receiver;

namespace RemSound.App;

/// <summary>
/// THE METRONOME IN THE DAW AND IN RECORDINGS (Ed, 2026-09-28: "you set whether you want to hear it in recording or daw").
/// A real plugin link and a stand-in plugin: "Metronome" offered only with "In the DAW", last in the list, its clicks on the
/// track placed for when the plugin plays them. And the recorder: with the tick on, the click in the recording where it
/// was heard, a track of its own when recording splits; off, nowhere.
/// </summary>
internal static partial class SelfTest
{
    /// <summary>
    /// METRONOME IN THE DAW: "Metronome" is offered to plugins only with "In the DAW", last, so no person's place in the
    /// list moves; 0.0.0.1 is refused as a peer's address; a stand-in plugin that takes it gets the clicks in its blocks,
    /// each heard within one DAW block of the beat when the plugin plays them its lead of blocks after asking (measured);
    /// RemSound's own outputs get no click meanwhile; back to "In RemSound", the track goes silent and the name goes.
    /// </summary>
    private static string? MetronomeInTheDaw()
    {
        Check(PeerAddress.ResolveHost("0.0.0.1") is null && PeerAddress.ResolveHost("0.0.0.1:47830") is null, "0.0.0.1 must never be taken as a peer's address");
        Check(PeerAddress.ResolveHost("0.0.0.2") is not null && PeerAddress.ResolveHost("192.168.1.50") is not null, "and only that address is refused");
        var logged = new List<string>();
        MainForm? form = null;
        PluginBridgeClient? plugin = null;
        // The link opens only when the machine's setting allows it, and a step before this one in a run may have left it
        // off (the feature pack, 2026-09-29): on for this step, as it was afterwards.
        var linkBefore = AppConfig.Load().EnableDawPluginLink;
        try
        {
            { var cfg = AppConfig.Load(); cfg.EnableDawPluginLink = true; cfg.Save(); }
            try { form = MetronomeWindow(logged); }
            catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }
            var main = form;
            main.SelectPeerForTest(new PeerAnnouncement(Guid.NewGuid(), "Zed", 47830, true, true, DateTime.UtcNow, IPAddress.Parse("192.168.77.61")));
            main.SelectPeerForTest(new PeerAnnouncement(Guid.NewGuid(), "Amy", 47830, true, true, DateTime.UtcNow, IPAddress.Parse("192.168.77.62")));
            var before = main.PeerListForPluginsForTest().Select(p => p.Name).ToList();
            Check(before.SequenceEqual(["Amy", "Zed"]), $"IN REMSOUND: no Metronome in the plugins' list ({string.Join(", ", before)})");
            main.ChangeMetronomeSettingsForTest(s => s.HearIn = MetronomeHearIn.Daw);
            var daw = main.PeerListForPluginsForTest();
            Check(daw.Select(p => p.Name).SequenceEqual(["Amy", "Zed", "Metronome"]) && daw[^1].Address.Equals(MetronomeDaw.PseudoPeer),
                $"IN THE DAW: Metronome at 0.0.0.1, last, the people where they were ({string.Join(", ", daw.Select(p => $"{p.Name} {p.Address}"))})");

            PluginBridgeClient.ResetLeadForTest();
            var host = Require(main.OpenPluginLinkForTest(0), "the plugin link must open on the step's own port");
            plugin = new PluginBridgeClient(host.Port);
            plugin.Hello();
            Check(WaitUntil(() => plugin.Connected && plugin.KnownPeers.Any(p => p.Name == "Metronome"), 3000), "a plugin must be offered Metronome");
            main.MetronomeStartStopForTest("the menu");
            Check(main.ReceiverForTest.Metronome.Schedule is null && !main.ReceiverForTest.OutputsRunning, "RemSound's own outputs get no click, and are not held open for it");
            plugin.SetReceivedPeers([MetronomeDaw.PseudoPeer]);

            // A DAW at 48 kHz with a 480-frame buffer: one call every 10 ms, paced by the clock, as its audio thread is.
            const int Frames = 480;
            var block = new float[Frames * 2];
            // The plugin plays each block its lead of blocks after asking. The lead starts at 3 and the plugin raises it,
            // process-wide, when replies come late (a busy machine); RemSound cannot see it, and places for the usual 3. So a
            // click is measured against where the lead in force puts it: at 3, within one block of the beat; at a raised
            // lead, that many blocks later. Only clicks after ten blocks of an unchanged lead count - a change of lead skips
            // or repeats a block in the plugin.
            var errors = new List<(int Lead, double Ms)>();
            var onsets = 0;
            var quietFrames = Frames * 20;
            var engine = main.MetronomeForTest;
            var tick = Stopwatch.Frequency / 100;
            var next = Stopwatch.GetTimestamp();
            var leads = new List<int>();
            for (var i = 0; i < 400; i++)
            {
                next += tick;
                while (Stopwatch.GetTimestamp() < next) Thread.SpinWait(50);
                var calledUs = MetronomeClock.NowUs();
                Array.Clear(block);
                plugin.ReadPeerBlock(block, Frames);
                var lead = PluginBridgeClient.LeadBlocks;
                leads.Add(lead);
                var measure = i >= 50 && leads.TakeLast(10).All(l => l == lead);   // the lead settles over the first blocks
                for (var f = 0; f < Frames; f++)
                {
                    var v = Math.Abs(block[2 * f]);
                    if (v >= 0.05f && quietFrames >= 48000 / 20 && measure)
                    {
                        onsets++;
                        var heard = calledUs + f * 1e6 / 48000;
                        var beat = engine.Timeline.BeatAtOrAfter(heard - 100_000);
                        var nearest = new[] { beat - 1, beat, beat + 1 }.Select(n => engine.Timeline.BeatTimeUs(n)).MinBy(t => Math.Abs(t - heard));
                        errors.Add((lead, (heard - nearest) / 1000));
                    }
                    quietFrames = v < 0.01f ? quietFrames + 1 : 0;
                }
            }
            Check(onsets >= 4, $"the track must get the clicks ({onsets} measured in 3.5 s at 120 bpm, leads {string.Join(",", leads.Distinct())})");
            var blockMs = Frames * 1000.0 / 48000;
            var worst = errors.Max(e => Math.Abs(e.Ms - (e.Lead - PluginBridgeClient.DefaultLeadBlocks) * blockMs));
            var atDefault = errors.Where(e => e.Lead == PluginBridgeClient.DefaultLeadBlocks).Select(e => Math.Abs(e.Ms)).DefaultIfEmpty(double.NaN).Max();
            Check(worst <= blockMs, $"each click heard within one DAW block ({blockMs:0} ms) of where the plugin's lead puts it - at its usual lead of 3, "
                + $"of the beat ({worst:0.0} ms at worst; {atDefault:0.0} ms at the usual lead; leads seen {string.Join(",", leads.Distinct())})");
            Check(main.ReceiverForTest.Metronome.Schedule is null, "and RemSound's outputs still get none");

            // Back to RemSound: the name goes, and a track still holding it hears nothing.
            main.ChangeMetronomeSettingsForTest(s => s.HearIn = MetronomeHearIn.RemSound);
            Check(!main.PeerListForPluginsForTest().Any(p => MetronomeDaw.IsPseudoPeer(p.Address)), "back in RemSound, Metronome leaves the list");
            var loud = 0f;
            for (var i = 0; i < 150; i++)
            {
                Thread.Sleep(10);
                plugin.ReadPeerBlock(block, Frames);
                if (i > 20) loud = Math.Max(loud, block.Max(Math.Abs));
            }
            Check(loud == 0f, $"and a track still on it gets silence ({loud:0.000})");
            main.MetronomeStartStopForTest("the menu");
            return $"offered only in the DAW, last; 0.0.0.1 refused as a peer; {onsets} clicks on the track, each within {worst:0.0} ms of where the plugin's "
                 + $"lead puts it ({atDefault:0.0} ms of the beat at the usual lead of 3; one {blockMs:0} ms block allowed; leads seen "
                 + $"{string.Join(",", leads.Distinct())}); RemSound's outputs silent of clicks; back in RemSound, gone";
        }
        finally
        {
            try { plugin?.Dispose(); } catch { /* teardown */ }
            try { form?.Dispose(); } catch { /* teardown */ }
            try { var cfg = AppConfig.Load(); cfg.EnableDawPluginLink = linkBefore; cfg.Save(); } catch { /* teardown */ }
        }
    }

    /// <summary>The clicks in a recorded WAV: the frame each starts on.</summary>
    private static List<long> RecordedOnsets(string path)
    {
        using var reader = new AudioFileReader(path);
        var onsets = new List<long>();
        var buffer = new float[reader.WaveFormat.Channels * 4800];
        long frame = 0;
        var quiet = long.MaxValue / 2;
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var i = 0; i < read; i += reader.WaveFormat.Channels, frame++)
            {
                var v = Math.Abs(buffer[i]);
                if (v >= 0.05f && quiet >= 48000 / 20) onsets.Add(frame);
                quiet = v < 0.01f ? quiet + 1 : 0;
            }
        }
        return onsets;
    }

    /// <summary>
    /// METRONOME RECORDING (the plan's test, measured): with "Include the click in RemSound's recordings" ticked, a single
    /// file carries the click on the exact sample the recording lane's output played it - the lane's read plus its delay -
    /// shaped or raw; a split recording has a Metronome track of its own with the same clicks; the tick off, neither has any.
    /// WASAPI only (the Mixed lane records, 20 ms delay), ASIO only (the ASIO lane, 5.3 ms) and both (the WASAPI lane).
    /// </summary>
    private static string? MetronomeInTheRecording()
    {
        var said = new List<string>();
        foreach (var configuration in AudioConfigurations.All)
        {
            var route = configuration == AudioConfiguration.WasapiOnly ? RenderRoute.Mixed
                : configuration == AudioConfiguration.AsioOnly ? RenderRoute.AsioLane : RenderRoute.WasapiLane;
            var delayUs = configuration == AudioConfiguration.AsioOnly ? 5_333.0 : 20_000.0;
            var what = configuration.Describe();
            var results = new List<string>();
            foreach (var (split, bypass, record) in new[] { (false, false, true), (false, true, true), (true, false, true), (false, false, false), (true, false, false) })
            {
                var temp = Path.Combine(Path.GetTempPath(), "remsound-metronome-rec-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(temp);
                using var receiver = new AudioReceiver();
                using var sender = new RemSound.Sender.AudioSender();
                try
                {
                    long clock = 7_000_000;
                    var run = MetronomeTimeline.Stopped(150_000, new MetronomeSignature(3, 4)).Start(clock);
                    var sound = Require(MetronomeSoundLibrary.Load("Click"), "the Click sound");
                    receiver.Metronome.Schedule = new MetronomeSchedule(run, null, MetronomeClockMap.Identity, sound, 0.7f, false, 1, () => clock);
                    receiver.Metronome.Record = record;
                    receiver.Metronome.RecordDelayUs = delayUs;
                    var peer = new IPEndPoint(IPAddress.Parse("192.168.1.70"), 47830);
                    var controller = new RecordingController(sender, receiver, new RemSoundSettingsStore("RemSound"), _ => { })
                    {
                        SettingsSourceForTest = () => new RecordingSettings
                        {
                            SplitTracks = split,
                            BypassShaping = bypass,
                            Source = split ? RecordingSource.Both : RecordingSource.ReceivedOnly,
                            FileFormat = RecordingFileFormat.Wav,
                            Folder = temp,
                        },
                        ConnectedPeersProvider = () => new[] { (peer.Address, "Peer") },
                    };
                    controller.Start();
                    Check(controller.IsRecording, $"{what}: premise - recording");
                    var silence = new float[480 * 2];
                    var clock0 = clock;
                    for (var i = 0; i < 300; i++)
                    {
                        // One render block of the recording lane: the shaped mix (single), the raw peer and the block's end.
                        if (!split && !bypass) receiver.OnReceivedSamples?.Invoke(silence.AsMemory(), route);
                        else
                        {
                            receiver.PeerRecordTapForTest?.Invoke(peer, silence.AsMemory());
                            receiver.OnRecordBlockComplete?.Invoke(silence.Length);
                        }
                        clock += 10_000;
                    }
                    controller.Stop();
                    var files = Directory.GetFiles(temp, "*.wav", SearchOption.AllDirectories);
                    var clickFile = split ? files.FirstOrDefault(f => Path.GetFileName(f).StartsWith("Metronome ", StringComparison.Ordinal))
                                          : files.SingleOrDefault();
                    var kind = split ? "split" : bypass ? "single, raw" : "single, shaped";
                    if (!record)
                    {
                        Check(split ? clickFile is null : RecordedOnsets(Require(clickFile, "the recording")).Count == 0,
                            $"{what}, {kind}: with the tick off there must be no click in the recording and no Metronome track");
                        results.Add($"{kind} off: none");
                        continue;
                    }
                    var onsets = RecordedOnsets(Require(clickFile, $"{what}, {kind}: the recording with the click"));
                    var expected = Enumerable.Range(0, 20).Select(n => run.BeatTimeUs(n))
                        .Where(t => t >= clock0 + delayUs && t < clock + delayUs)
                        .Select(t => (long)Math.Round((t - (clock0 + delayUs)) * 48000 / 1e6)).ToList();
                    Check(onsets.SequenceEqual(expected), $"{what}, {kind}: every click on the sample its output played it ({string.Join(", ", onsets.Take(5))} against {string.Join(", ", expected.Take(5))})");
                    if (split)
                        Check(RecordedOnsets(files.First(f => Path.GetFileName(f).StartsWith("Peer ", StringComparison.Ordinal))).Count == 0, $"{what}: the peer's own track holds no click");
                    results.Add($"{kind}: {onsets.Count} clicks exact");
                }
                finally
                {
                    try { Directory.Delete(temp, recursive: true); } catch { /* best effort */ }
                }
            }
            said.Add($"{what} ({string.Join("; ", results)})");
        }
        return string.Join(" | ", said);
    }
}
