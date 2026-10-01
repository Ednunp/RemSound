using RemSound.Core;
using RemSound.Sender;

namespace RemSound.App;

internal static partial class SelfTest
{
    /// <summary>METRONOME CAPTURE TIMES (2026-09-29, PLAN-metronome-server-2026-09-29.md; Ed: "you need to find a way that is
    /// as accurate as possible without forcing people to mess about with tests"). The server times each computer from its
    /// own sound: the sender says when each packet's first sample reached the input, and the server pairs that with when the
    /// packet arrived. So the time must be the input's - the block's time less its own lane's input delay, then on by the
    /// packet's place in the block - and the sequence must be the one the packet went out with. With both kinds of capture
    /// the ASIO lane's is reported: the instrument. The clock here moves exactly with the sound fed, so each packet's time is
    /// known to the microsecond; blocks that do not line up with the packets show a packet's place counted wrong.</summary>
    private static string? MetronomeCaptureTimesForTheServer()
    {
        const double WasapiMs = 40, AsioMs = 25;        // large and different: a delay missed, or the other lane's, shows
        const int WasapiBlock = 144, AsioBlock = 96;     // 3 ms and 2 ms: whole microseconds, never in step with a packet
        var (key, fingerprint) = RemSoundCrypto.ForPlainPassword("metronome-capture-times");
        var said = new List<string>();
        foreach (var configuration in AudioConfigurations.All)
        {
            var what = configuration.Describe();
            using var sink = new WireSink();
            using var sender = new AudioSender();
            sender.SetReceivers([sink.Endpoint]);
            sender.ConfigureCodec(AudioTransportCodec.Opus, 480);
            sender.AudioKey = key;
            sender.AudioFingerprint = fingerprint;
            var asio = configuration.UsesAsio();
            var wasapi = configuration.UsesWasapi() || !asio;
            sender.WasapiCaptureLatencyMsForTest = WasapiMs;
            sender.AsioCaptureLatencyMsForTest = asio ? AsioMs : 0;
            sender.UseDefaultLaneForTest(asio ? RenderRoute.WasapiLane : RenderRoute.Mixed);
            if (asio) sender.UseAsioLaneForTest();

            // One clock for both lanes, each block fed when its time comes round - but, as a real capture thread, late by
            // up to 4 ms most times, and on time only now and then (Ed, 2026-09-29: "see if that code to lock can be made
            // better"). What the server is told must be the input's time all the same, not the thread's.
            const long Start = 900_000_000;
            var now = Start;
            var worstLateUs = 0L;
            var late = new Random(29);
            var blocks = 0;
            sender.CaptureClockForTest = () => now;
            long nextAsio = Start, nextWasapi = Start;
            void Feed(long untilUs)
            {
                while (true)
                {
                    var asioDue = asio ? nextAsio : long.MaxValue;
                    var wasapiDue = wasapi ? nextWasapi : long.MaxValue;
                    var due = Math.Min(asioDue, wasapiDue);
                    if (due >= untilUs) return;
                    var lateUs = blocks++ % 9 == 0 ? 0 : late.Next(0, 4000);
                    worstLateUs = Math.Max(worstLateUs, lateUs);
                    now = due + lateUs;
                    if (asioDue <= wasapiDue) { sender.FeedAsioLaneForTest(new float[AsioBlock * 2]); nextAsio += AsioBlock * 1000 / 48; }
                    else { sender.FeedDefaultLaneForTest(new float[WasapiBlock * 2]); nextWasapi += WasapiBlock * 1000 / 48; }
                }
            }

            Feed(Start + 60_000);
            Check(sender.TakeTimingReports().Count == 0, $"{what}: OFF: not on a server, nothing must be noted");
            sender.TimingWanted = true;
            Feed(Start + 300_000);
            var reports = sender.TakeTimingReports();
            Check(sender.TakeTimingReports().Count == 0, $"{what}: a report's packets are forgotten once taken");
            Thread.Sleep(150);

            var lane = asio ? "ASIO" : "WASAPI";
            var stream = asio ? sender.AsioLaneStreamIdForTest : sender.DefaultLaneStreamIdForTest;
            var delayUs = (long)((asio ? AsioMs : WasapiMs) * 1000);
            Check(reports.Count >= 8, $"{what}: ON: the newest packets must be noted ({reports.Count})");
            Check(reports.All(r => r.Stream == stream),
                $"{what}: THE INSTRUMENT: the {lane} lane's packets must be reported{(asio && configuration.UsesWasapi() ? ", not the WASAPI lane's beside it" : "")} "
                + $"(streams {string.Join(", ", reports.Select(r => r.Stream).Distinct())}; the {lane} lane is {stream})");
            var wire = new List<uint>();
            foreach (var p in sink.Snapshot())
                if (RemPacket.TryReadHeader(p, out var type, out var id, out var sequence) && type == RemPacketType.Audio && id == stream)
                    wire.Add(sequence);
            Check(wire.Count > 0 && reports.All(r => wire.Contains(r.Sequence)),
                $"{what}: each reported sequence must be a packet that went out ({reports.Count} reported, {wire.Count} on the wire)");
            var first = wire.Min();
            var steps = reports.Zip(reports.Skip(1), (x, y) => (y.CapturedUs - x.CapturedUs) / (double)(y.Sequence - x.Sequence)).ToList();
            var frameUs = steps.Count > 0 ? steps[0] : 0;
            Check(frameUs > 0 && steps.All(s => Math.Abs(s - frameUs) <= 2),
                $"{what}: EVERY PACKET'S PLACE: the times must move on by one packet's length each time, whichever block a packet started in "
                + $"({string.Join(", ", steps.Take(8).Select(s => $"{s / 1000:0.000} ms"))})");
            var worst = reports.Max(r => Math.Abs(r.CapturedUs - (Start - delayUs + (long)Math.Round((r.Sequence - first) * frameUs))));
            Check(worst <= 2,
                $"{what}: THE INPUT'S TIME: each packet's time must be when its first sample reached the input - the {lane} lane's own "
                + $"{(asio ? AsioMs : WasapiMs)} ms before the block it came in, exactly, however late the capture thread ran "
                + $"(up to {worstLateUs / 1000.0:0.0} ms here; worst {worst / 1000.0:0.000} ms out)");
            var expectSource = asio ? $"the ASIO input, its delay taken as {AsioMs:0.0} ms" : $"the WASAPI input, its delay taken as {WasapiMs:0.0} ms";
            Check(sender.TimingSourceForLog?.StartsWith(expectSource, StringComparison.Ordinal) == true,
                $"{what}: THE LOG: it must say which input the server times it by, and the delay taken on trust ({sender.TimingSourceForLog})");
            said.Add($"{what}: the {lane} lane, {reports.Count} packets of {frameUs / 1000:0.##} ms, within {worst} us with the thread up to {worstLateUs / 1000.0:0.0} ms late");
        }
        return "off, nothing noted; on, each packet's time is when its first sample reached the input - its own lane's delay "
             + "taken off, its place in the block counted, the capture thread's lateness left out - with the sequence it went out "
             + "with, and the ASIO lane's when both run: " + string.Join("; ", said);
    }
}
