using System.Net;
using RemSound.Core;
using RemSound.Receiver;
using RemSound.Sender;

namespace RemSound.App;

/// <summary>
/// EACH LANE ITS OWN CAPTURE DELAY (N7, Ed 2026-09-28: "FIX IT... that's going to be important later. I HAVE A PLAN").
/// A sender's format packet says how long its capture took, so the far end's latency readout can count a stage it cannot
/// see. Every lane used to say one figure - the ASIO input's whenever ASIO ran - so with both kinds of capture the WASAPI
/// stream claimed ASIO's 1 ms for a capture of 10 or more, and the plugin lane claimed a device it never touches. Now each
/// lane says its own: the WASAPI capture's, the ASIO input's, and the DAW block the plugin link hands over. The value only;
/// the packet's layout is what every port already reads.
/// </summary>
internal static partial class SelfTest
{
    private static string? EachLaneAnnouncesItsOwnCaptureDelay()
    {
        // What each capture says of itself here: a 64-frame ASIO buffer, and a WASAPI engine period with the buffer asked for.
        const double AsioMs = 1.33, WasapiMs = 10.67;
        const int DawBlock = 256;                       // the plugin lane's block: 5.33 ms at 48 kHz
        var dawMs = DawBlock * 1000.0 / 48000;
        const double Tolerance = 0.06;                  // the wire carries the figure in 1/20 ms steps
        var (key, fingerprint) = RemSoundCrypto.ForPlainPassword("lane-capture-delay");
        var said = new List<string>();
        foreach (var configuration in AudioConfigurations.All)
        {
            var what = configuration.Describe();
            using var sink = new WireSink();
            using var sender = new AudioSender();
            sender.SetReceivers([sink.Endpoint]);
            sender.ConfigureCodec(AudioTransportCodec.Opus, 120);
            sender.AudioKey = key;
            sender.AudioFingerprint = fingerprint;
            // The lanes as the configuration has them: WASAPI only - one lane, announced Mixed, and no ASIO driver open;
            // ASIO only - the ASIO lane, the WASAPI lane idle beside it; both - both lanes.
            var asio = configuration.UsesAsio();
            sender.WasapiCaptureLatencyMsForTest = WasapiMs;
            sender.AsioCaptureLatencyMsForTest = asio ? AsioMs : 0;
            sender.UseDefaultLaneForTest(asio ? RenderRoute.WasapiLane : RenderRoute.Mixed);
            if (asio) sender.UseAsioLaneForTest();
            sender.UsePluginLaneForTest(asio ? RenderRoute.Mixed : RenderRoute.AsioLane);   // as SetAudioMode places it
            var callback = new float[64 * 2];
            for (var i = 0; i < 4; i++)
            {
                if (asio) sender.FeedAsioLaneForTest(callback);
                if (configuration.UsesWasapi() || !asio) sender.FeedDefaultLaneForTest(new float[480 * 2]);
            }
            sender.SetPluginSendActive(true);
            sender.SubmitPluginBlock(new float[DawBlock * 2]);
            Thread.Sleep(150);

            // Each stream's format, by the lane it announces.
            var formats = new Dictionary<ushort, AudioFormatInfo>();
            foreach (var p in sink.Snapshot())
                if (RemPacket.TryReadHeader(p, out var type, out var stream, out _) && type == RemPacketType.Format
                    && RemPacket.TryReadFormat(p.AsSpan(RemPacket.HeaderSize), out var f, out _))
                    formats[stream] = f;
            AudioFormatInfo? Of(ushort stream) => formats.TryGetValue(stream, out var f) ? f : null;
            var asioFormat = Of(sender.AsioLaneStreamIdForTest);
            var wasapiFormat = Of(sender.DefaultLaneStreamIdForTest);
            var pluginFormat = Of(sender.PluginLaneStreamIdForTest);

            if (asio)
                Check(asioFormat is not null && Math.Abs(asioFormat.CaptureLatencyMs - AsioMs) < Tolerance,
                    $"{what}: the ASIO lane's format must carry the ASIO input's own {AsioMs} ms (it said {asioFormat?.CaptureLatencyMs.ToString("0.00") ?? "nothing"})");
            if (configuration.UsesWasapi() || !asio)
                Check(wasapiFormat is not null && Math.Abs(wasapiFormat.CaptureLatencyMs - WasapiMs) < Tolerance,
                    $"{what}: the WASAPI lane's format must carry the WASAPI capture's own {WasapiMs} ms, not the ASIO input's "
                    + $"(it said {wasapiFormat?.CaptureLatencyMs.ToString("0.00") ?? "nothing"})");
            if (asio && configuration.UsesWasapi())
                Check(Math.Abs(asioFormat!.CaptureLatencyMs - wasapiFormat!.CaptureLatencyMs) > 5,
                    $"{what}: the two lanes' formats must carry DIFFERENT figures - each its own capture ({asioFormat.CaptureLatencyMs:0.00} and {wasapiFormat.CaptureLatencyMs:0.00} ms)");
            Check(pluginFormat is not null && Math.Abs(pluginFormat.CaptureLatencyMs - dawMs) < Tolerance,
                $"{what}: the plugin lane's format must carry the DAW block it was handed, {dawMs:0.00} ms - the host owns the device and says "
                + $"no more - not a capture device it never touches (it said {pluginFormat?.CaptureLatencyMs.ToString("0.00") ?? "nothing"})");

            // The far end: the same format packets into a receiver, and its readout's capture term for each output - the
            // stream sent for that output, so the ASIO output's journey starts at the far ASIO input.
            using var receiver = new AudioReceiver();
            receiver.SetOutputDevices([]);
            receiver.SetPlaybackEnabled(true);
            receiver.AudioKey = key;
            receiver.AudioFingerprint = fingerprint;
            var from = new IPEndPoint(IPAddress.Parse("10.77.0.7"), 47830);
            foreach (var p in sink.Snapshot())
                if (RemPacket.TryReadHeader(p, out var type, out _, out _) && type == RemPacketType.Format)
                    receiver.InjectExternalPacket(p, p.Length, from);
            using var here = new AudioSender();   // this end captures nothing, so only the far end's figures count
            const double Estimate = 99;
            var asioReadout = MainForm.LaneCaptureMs(receiver, here, RenderRoute.AsioLane, Estimate);
            var wasapiReadout = MainForm.LaneCaptureMs(receiver, here, RenderRoute.WasapiLane, Estimate);
            if (asio)
                Check(Math.Abs(asioReadout - AsioMs) < Tolerance,
                    $"{what}: the far end's ASIO output must count the ASIO stream's own capture, {AsioMs} ms (it counted {asioReadout:0.00})");
            if (configuration.UsesWasapi())
                Check(Math.Abs(wasapiReadout - WasapiMs) < Tolerance,
                    $"{what}: the far end's WASAPI output must count the WASAPI stream's own capture, {WasapiMs} ms (it counted {wasapiReadout:0.00})");
            Check(Math.Abs(receiver.AnnouncedCaptureLatencyMs - formats.Values.Max(f => f.CaptureLatencyMs)) < Tolerance,
                $"{what}: the whole-journey figure must still be the longest any stream stated ({receiver.AnnouncedCaptureLatencyMs:0.00})");
            said.Add($"{what}: ASIO lane {asioFormat?.CaptureLatencyMs.ToString("0.00") ?? "-"}, WASAPI lane {wasapiFormat?.CaptureLatencyMs.ToString("0.00") ?? "-"}, "
                     + $"plugin {pluginFormat?.CaptureLatencyMs:0.00} ms; the far end's ASIO output counts {asioReadout:0.00}, its WASAPI output {wasapiReadout:0.00}");
        }
        return string.Join("; ", said);
    }
}
