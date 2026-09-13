using RemSound.Core;

namespace RemSound.App;

internal static partial class SelfTest
{
    /// <summary>
    /// A FLOOR LEARNED ABOUT A DEVICE THAT HAS GONE IS NOT EVIDENCE ABOUT THE ONE THAT COMES BACK.
    ///
    /// <para>Found in Ed's overnight log, 2026-09-09, by the long-run line built the day before. The
    /// WASAPI learned floor sat at <b>25 ms for seven and a half hours on a lane with no device at
    /// all</b> — no audio, no underruns, not a single render read. It was learned during six noisy
    /// minutes at the start of the evening, and then froze.</para>
    ///
    /// <para><b>Why it froze.</b> The floor only relaxes on CLEAN TICKS, and a lane with no device
    /// produces no ticks to be clean. And the only thing that reset a lane's memory was a new stream
    /// SESSION opening — which an output disappearing is not. The peer never stopped sending; it was
    /// the speaker that left. So the floor survived the entire absence and would have been applied,
    /// before anything was measured, the moment a device returned.</para>
    ///
    /// <para><b>Why it matters.</b> The floor's whole job is to stop the tuner descending below what a
    /// particular output can survive. Applied to a DIFFERENT output — or the same one after hours away
    /// and a hibernate — it is not a safety net, it is a latency figure with no evidence behind it,
    /// arriving before the first measurement. That is the shape of every report in this saga: fine
    /// when you left it, slow when it came back.</para>
    ///
    /// <para><b>Both lanes.</b> WASAPI is where outputs vanish most — a Bluetooth headset powering
    /// down, a USB interface unplugged, a resume from hibernate — but an ASIO driver can be lost too,
    /// and a floor learned about a departed ASIO device is exactly as stale. The rule is per lane and
    /// identical on both, so the test drives both.</para>
    ///
    /// <para>Not otherwise per-configuration: this is one lane's memory reacting to its own output
    /// coming and going. Which of the three configurations is live decides WHICH lanes exist, and the
    /// test covers each lane that can exist.</para>
    /// </summary>
    /// <summary>A lane memory on its own, for the gate. The type is nested in MainForm because it is
    /// MainForm state; this exists so the rule can be driven without a window.</summary>
    private static MainForm.LaneTuneMemory NewLaneMemoryForTest() => new();

    private static string? AuditReturningOutputForgetsItsOldFloor()
    {
        var results = new List<string>();
        foreach (var lane in new[] { "WASAPI", "ASIO" })
        {
            var memory = NewLaneMemoryForTest();

            // The lane is playing, and learns a floor the honest way.
            memory.NoteLaneAudible(true);
            memory.Creep.NoteShortfallAt(atMs: 40, justifiedMs: 30);
            var learned = memory.Creep.DiscoveredFloorMs;
            Check(learned > 0, $"{lane}: the lane must be able to learn a floor in the first place (got {learned} ms)");

            // THE OUTPUT GOES AWAY. Nothing else changes — the peer keeps sending, the session is the
            // same session. Going quiet must NOT itself wipe what the lane knows: a lane that is merely
            // paused for a moment should not lose its evidence, which is why the reset happens on the
            // way back rather than on the way out.
            memory.NoteLaneAudible(false);
            Check(memory.Creep.DiscoveredFloorMs == learned,
                $"{lane}: a lane going quiet must not, by itself, throw away what it learned (floor went "
                + $"{learned} -> {memory.Creep.DiscoveredFloorMs} ms) — a brief gap should cost nothing");

            // Hours pass. No ticks at all, so nothing relaxes it — this is the seven and a half hours
            // the field log recorded.
            for (var i = 0; i < 5000; i++) memory.NoteLaneAudible(false);
            Check(memory.Creep.DiscoveredFloorMs == learned,
                $"{lane}: with no device there are no ticks, so nothing relaxes the floor — it is still {learned} ms, "
                + "which is exactly what the overnight log showed and why this cannot be left to the creep");

            // THE OUTPUT COMES BACK. This is the moment that matters.
            var forgot = memory.NoteLaneAudible(true);
            Check(forgot,
                $"{lane}: an output returning after an absence must be reported as forgetting its floor, so the log says "
                + "it happened — a silent reset is one nobody can check afterwards");
            Check(memory.Creep.DiscoveredFloorMs == 0,
                $"{lane}: a returning output must re-earn its floor from scratch (still {memory.Creep.DiscoveredFloorMs} ms) "
                + "— applying a number learned about hardware that has been gone for hours is a latency figure with no "
                + "evidence behind it, arriving before the first measurement");
            Check(memory.NeedsUnderrunBaseline,
                $"{lane}: the underrun counter must be re-baselined too — counts from before the absence describe the "
                + "old device, and a stale baseline reads as a burst of underruns that never happened");

            // AND IT MUST NOT KEEP FIRING. Staying audible is not a return; only the transition is.
            Check(!memory.NoteLaneAudible(true),
                $"{lane}: a lane that simply stays audible must not keep resetting ({lane} reported a second forget) — "
                + "that would wipe the floor every second and the lane could never learn anything at all");

            // A lane that never learned anything has nothing to forget, and must not clutter the log
            // saying so.
            var fresh = NewLaneMemoryForTest();
            fresh.NoteLaneAudible(true);
            fresh.NoteLaneAudible(false);
            Check(!fresh.NoteLaneAudible(true),
                $"{lane}: a lane with no learned floor must not announce forgetting one");

            results.Add($"{lane}: {learned} ms held across the absence, forgotten on return");
        }

        return string.Join("; ", results);
    }
}
