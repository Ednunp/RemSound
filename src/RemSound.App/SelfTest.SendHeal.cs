using System.Collections.Concurrent;
using RemSound.Core;
using RemSound.Sender;

namespace RemSound.App;

/// <summary>
/// The sending side heals one source at a time (review 2026-09-25; Ed: "yes"). A WASAPI send source that failed to open
/// or start stayed silent until it was unticked and ticked again; one that died made the whole sender restart, which closed
/// and re-opened every healthy source and gave the ASIO stream a fresh start at the far end. Driven through the real
/// composite capture on this machine's real output devices, capturing their loopback, silently.
/// </summary>
internal static partial class SelfTest
{
    /// <summary>The first two real output devices this machine has, for loopback capture.</summary>
    private static List<CaptureSourceSpec> LoopbackSpecs(int wanted)
    {
        var specs = new List<CaptureSourceSpec>();
        try
        {
            foreach (var output in AudioDeviceCatalog.LoadOutputs())
            {
                if (output.DeviceId is null) continue;
                specs.Add(new CaptureSourceSpec(output.DeviceId, CaptureKind.Loopback, output.Name));
                if (specs.Count == wanted) break;
            }
        }
        catch { /* none */ }
        return specs;
    }

    /// <summary>
    /// A SEND SOURCE THAT FAILED IS TRIED AGAIN UNTIL IT OPENS.
    ///
    /// <para>A source refused as a busy or exclusively held device refuses (MixingEngine.RefuseOpenForTest), then allowed:
    /// through the mixer with another source beside it, and through the single-source fast path. It must come back on its
    /// own within a few beats, with the working source left alone, and the log must say both that it keeps being tried
    /// and that it came back.</para>
    /// </summary>
    private static string? AFailedSendSourceIsTriedAgain()
    {
        var specs = LoopbackSpecs(2);
        if (specs.Count == 0) return Skip("no output device here to capture from");
        var restoreBeat = CompositeCaptureBackend.HealIntervalMs;
        CompositeCaptureBackend.HealIntervalMs = 200;
        var refused = new ConcurrentDictionary<string, bool>();
        MixingEngine.RefuseOpenForTest = spec => refused.ContainsKey(spec.DeviceId);
        var summary = new List<string>();
        try
        {
            // Through the mixer: the last device refused. With two devices the first keeps capturing beside it.
            var lines = new ConcurrentQueue<string>();
            var failing = specs[^1];
            refused[failing.DeviceId] = true;
            using (var composite = new CompositeCaptureBackend(AudioMode.WasapiOnly, null, _ => { }, null, null, lines.Enqueue, useTightLatencyWasapi: false))
            {
                composite.Start(specs);
                var mixing = Require(composite.WasapiLaneForTest as MixingEngine, "the mixer lane was not built");
                var failingKey = $"{failing.DeviceId}|{CaptureKind.Loopback}";
                Check(mixing.ActiveSourcesForTest.All(a => a.Key != failingKey), "premise: the refused source must not be capturing");
                var before = mixing.ActiveSourcesForTest.ToList();
                // The heal's own 200 ms beat, until it has tried and logged it - not a fixed 900 ms (2026-09-28).
                WaitFor(() => lines.Any(l => l.Contains("still cannot be opened", StringComparison.Ordinal)), TimeSpan.FromSeconds(3));
                Check(lines.Any(l => l.Contains("still cannot be opened", StringComparison.Ordinal)),
                    $"the log must say the failed source keeps being tried (it said: {string.Join(" | ", lines.TakeLast(3))})");
                Check(composite.IsRunning, "one source failing must not make the whole send look stopped");
                refused.TryRemove(failing.DeviceId, out _);
                Check(WaitFor(() => mixing.ActiveSourcesForTest.Count == specs.Count, TimeSpan.FromSeconds(5)),
                    $"THE SILENT SOURCE: once the device can be opened, the source must come back by itself, without being unticked "
                  + $"and ticked again ({mixing.ActiveSourcesForTest.Count} of {specs.Count} capturing)");
                // The source is back a moment before the heal writes its line: wait for the line, not just the source.
                Check(WaitFor(() => lines.Any(l => l.Contains("is capturing again", StringComparison.Ordinal)), TimeSpan.FromSeconds(3)),
                    "and the log must say it came back");
                if (specs.Count > 1)
                {
                    var after = mixing.ActiveSourcesForTest;
                    Check(before.All(b => after.Any(a => a.Key == b.Key && ReferenceEquals(a.Source, b.Source))),
                        "and the source that was working all along must have been left alone, not re-opened");
                }
                summary.Add(specs.Count > 1 ? "through the mixer beside a working source" : "through the mixer");
            }

            // Through the single-source fast path (locked to the audio clock), which had the same gap.
            var pushLines = new ConcurrentQueue<string>();
            var only = specs[0];
            refused[only.DeviceId] = true;
            using (var composite = new CompositeCaptureBackend(AudioMode.WasapiOnly, null, _ => { }, null, null, pushLines.Enqueue, useTightLatencyWasapi: true))
            {
                composite.Start([only]);
                var push = Require(composite.WasapiLaneForTest as PushModeWasapiBackend, "the single-source fast path was not chosen");
                Check(!push.IsRunning, "premise: the refused source must not be capturing");
                Check(WaitFor(() => pushLines.Any(l => l.Contains("still cannot be opened", StringComparison.Ordinal)), TimeSpan.FromSeconds(3)),
                    "on the fast path, the heal's beat must try it again and say it still cannot be opened");
                refused.TryRemove(only.DeviceId, out _);
                Check(WaitFor(() => push.IsRunning, TimeSpan.FromSeconds(5)),
                    "THE SILENT SOURCE: on the fast path too, a source that failed must come back by itself once it can be opened");
                Check(WaitFor(() => pushLines.Any(l => l.Contains("is capturing again", StringComparison.Ordinal)), TimeSpan.FromSeconds(3)),
                    "and the log must say so");
                summary.Add("through the single-source fast path");
            }
            return "a refused send source is tried every few seconds, logged as still failing, and comes back by itself once it "
                 + "can be opened - " + string.Join(", and ", summary);
        }
        finally
        {
            MixingEngine.RefuseOpenForTest = null;
            CompositeCaptureBackend.HealIntervalMs = restoreBeat;
        }
    }

    /// <summary>
    /// A SEND SOURCE THAT DIES IS RE-OPENED ALONE; THE REST, AND ASIO, CARRY ON.
    ///
    /// <para>A source that dies in place made the capture report itself stopped, and the main window restarted the whole
    /// sender: every healthy WASAPI source closed and re-opened, and - in "both" - the ASIO stream given a fresh start at
    /// the far end (only a sender Start does that). Now the capture stays "running", so no restart happens, and the heal
    /// re-opens just the dead source.</para>
    /// </summary>
    private static string? ADeadSendSourceIsReopenedAlone()
    {
        var specs = LoopbackSpecs(2);
        if (specs.Count == 0) return Skip("no output device here to capture from");
        var restoreBeat = CompositeCaptureBackend.HealIntervalMs;
        CompositeCaptureBackend.HealIntervalMs = 60_000;   // healed by hand below, so nothing happens between the checks
        try
        {
            var lines = new ConcurrentQueue<string>();
            using (var composite = new CompositeCaptureBackend(AudioMode.WasapiOnly, null, _ => { }, null, null, lines.Enqueue, useTightLatencyWasapi: false))
            {
                composite.Start(specs);
                var mixing = Require(composite.WasapiLaneForTest as MixingEngine, "the mixer lane was not built");
                Check(mixing.ActiveSourcesForTest.Count == specs.Count, "premise: every source capturing");
                var dying = specs[^1];
                var before = mixing.ActiveSourcesForTest.ToList();
                mixing.FaultSourceForTest(dying.DeviceId);
                Check(composite.IsRunning && !composite.HasFaulted,
                    "THE FULL RESTART: one source dying must not make the capture report itself stopped - that is what restarted the "
                  + "whole sender, every healthy source and the ASIO stream with it");
                composite.HealNowForTest();
                var after = mixing.ActiveSourcesForTest;
                var dyingKey = $"{dying.DeviceId}|{CaptureKind.Loopback}";
                var dead = before.First(b => b.Key == dyingKey).Source;
                Check(after.Any(a => a.Key == dyingKey && !ReferenceEquals(a.Source, dead)), "the dead source must be re-opened");
                Check(before.Where(b => b.Key != dyingKey).All(b => after.Any(a => ReferenceEquals(a.Source, b.Source))),
                    "and every other source left exactly as it was");
            }

            using (var composite = new CompositeCaptureBackend(AudioMode.WasapiOnly, null, _ => { }, null, null, lines.Enqueue, useTightLatencyWasapi: true))
            {
                composite.Start([specs[0]]);
                var push = Require(composite.WasapiLaneForTest as PushModeWasapiBackend, "the single-source fast path was not chosen");
                Check(push.IsRunning, "premise: capturing");
                push.FaultForTest();
                Check(composite.IsRunning, "on the fast path too, the capture must not report itself stopped");
                composite.HealNowForTest();
                Check(push.IsRunning && !push.HasFaulted, "and the heal must bring the source back");
            }

            // AND ASIO CARRIES ON (2026-09-28: this was a regex over the source - no ASIO lane was ever present). The real sender
            // with an ASIO lane beside the WASAPI sources, on a driver that cannot exist: a WASAPI source dies and is healed, and
            // the ASIO stream keeps the stream id it had - so the far end hears no new stream.
            using (var sender = new AudioSender())
            {
                sender.SetAudioMode(AudioMode.BothIndependent, "RemSound self-test — no such ASIO driver");
                sender.Configure(specs);
                sender.Start();
                Check(sender.PersistentAsioForTest is not null, "premise: the ASIO lane must be there beside the WASAPI sources");
                var composite = Require(sender.EngineForTest as CompositeCaptureBackend, "the sender's capture must be the composite");
                var mixing = Require(composite.WasapiLaneForTest as MixingEngine, "the WASAPI side must be the mixer (two sources)");
                var asioStream = sender.AsioLaneStreamIdForTest;
                mixing.FaultSourceForTest(specs[^1].DeviceId);
                composite.HealNowForTest();
                Check(composite.IsRunning && sender.AsioLaneStreamIdForTest == asioStream,
                    $"ASIO CARRIES ON: a WASAPI source dying and healing must leave the ASIO stream as it was ({asioStream} became {sender.AsioLaneStreamIdForTest})");
                sender.Stop();
                sender.Start();
                Check(sender.AsioLaneStreamIdForTest != asioStream, "premise: a real restart of the sender does give the ASIO stream a fresh start - so the check above could fail");
            }
            return "a dead source leaves the capture 'running', so nothing restarts the sender, and the heal re-opens only that source - "
                 + "through the mixer, every other source untouched, and on the fast path; with an ASIO lane beside them, its stream carries on unchanged";
        }
        finally { CompositeCaptureBackend.HealIntervalMs = restoreBeat; }
    }
}
