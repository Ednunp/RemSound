using System.Text.RegularExpressions;
using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// EVERY TEST THAT TOUCHES THE WASAPI/ASIO AXIS COVERS ALL THREE CONFIGURATIONS.
///
/// <para>Ed, 2026-08-24, after correcting the same mistake twice in two days: "there are 3 modes of
/// configuration. asio, wasapi and 2 lane. why do you never ever ever take this into consideration?
/// ... you should never ever ever ever need me to correct you on this again."</para>
///
/// <para><b>Why a note was not enough.</b> The three configurations were written into a memory file
/// on the 23rd and the same mistake was made again on the 24th. A rule that lives in a document is
/// something to remember; this is the same shape as the event-coverage and control-completeness
/// guards, which do not ask anyone to remember anything — a new handler or a new control turns the
/// gate red until somebody has said how it is covered. This does that for the audio axis.</para>
///
/// <para><b>How it works.</b> It reads the gate's OWN SOURCE and finds every test method that
/// mentions the WASAPI/ASIO axis — a lane, an audio mode, a driver. Each of those must either loop
/// over <see cref="AudioConfigurations.All"/>, exercise all three by name, or be listed below with a
/// written reason it needs only one. A new test that touches the axis and does neither fails the
/// build. Stale exemptions fail it too, so the list cannot quietly accumulate reassurance about
/// tests that have gone or changed.</para>
///
/// <para><b>Why by method and not by step name.</b> A step's NAME is a claim about what it does; its
/// BODY is what it does. Matching on the name is how "no silently-missing term" ended up passing for
/// months over a term that was silently missing.</para>
/// </summary>
internal static partial class SelfTest
{
    /// <summary>A test method that mentions the audio axis but is deliberately single-configuration.
    /// <paramref name="Reason"/> must say why one is genuinely enough — not that covering three would
    /// be inconvenient.</summary>
    private sealed record AudioAxisExemption(string Method, string Reason);

    /// <summary>
    /// Tests that touch the axis and legitimately need only one configuration.
    ///
    /// <para>The bar: the test must be about a MECHANISM that cannot differ between configurations —
    /// a wire format, a lifecycle, a piece of arithmetic — rather than about behaviour a user
    /// experiences differently depending on what they have ticked. If in doubt, cover all three;
    /// this list is the exception, not the escape hatch.</para>
    /// </summary>
    private static readonly AudioAxisExemption[] AudioAxisExemptions =
    [
        // ---- Driver and device lifecycle. About opening and closing hardware, not about which
        // combination a user has selected. --------------------------------------------------------
        new("AsioApartmentThread", "the apartment thread's contract is the same however many lanes exist"),
        new("AuditAsioParkStopsDelivery", "parking is a property of the ASIO capture lane itself; there is no WASAPI equivalent to compare"),

        // ---- Pure arithmetic and decision cores, tested by feeding values directly. Covering three
        // configurations would mean feeding the same function the same numbers three times. --------
        new("AuditLatencyUsesDeviceReportedFigures", "pure arithmetic over supplied values — the configuration is the caller's business, and AuditOutputLatencyIsReportedPerLane covers the picking"),
        new("AuditAudioLoopsAreNotAsync", "a reflection check on two methods' compiler attributes; no audio runs at all"),

        // ---- Plugin. One instance is one track and one peer; the DAW is the device, so the machine's
        // own output configuration is not part of what is being tested. ---------------------------
        new("PluginBridgeEndToEnd", "the bridge carries raw float between app and DAW; the machine's output configuration is not in that path"),
        new("PluginRateConversion", "resampler arithmetic between host rate and the 48k wire; no lane involved"),
        new("PluginSurvivesSessionChurn", "claim/release lifecycle in the bridge, not a rendering path"),
        // PluginDoubleAudioGuard is NOT exempt: the doubling bug it now also guards against was
        // configuration-specific (it only bit with both kinds of output ticked), so it covers three.

        // ---- Settings, profiles and lists. Persistence and list semantics are identical whatever is
        // ticked; the ticked state is DATA these round-trip, not behaviour that varies. -------------
        new("ProfileRoundTrip", "settings persistence — the audio mode is a value being saved, not behaviour under test"),
        new("MainWindowProfileRoundTrip", "the window's save/reload path; the configuration is one of the values it carries"),
        new("ProfileDriftTripwire", "reflects over every settings property to catch one that stops persisting"),
        new("AsioTickMemory", "per-driver tick memory is about remembering a selection, not about rendering it"),
        new("SendAppListSemantics", "the active/remembered app lists are WASAPI-only by construction"),
        new("AppSendEnumeration", "enumerates audio sessions; per-application capture exists only on the WASAPI path"),
        new("DefaultOutputFollower", "the 'follow the Windows default' entry is a WASAPI device concept — ASIO has no default"),

        // ---- Service. Send-only, WASAPI-only, and it never renders — so it has no output
        // configuration at all. ---------------------------------------------------------------------
        new("ServiceProfileIsolation", "the service profile is a separate store; the service never renders"),
        new("ServiceSendHostStream", "the service is WASAPI send-only and has no output lane"),
        new("ServiceSenderParity", "compares service and app SENDER settings; the receive configuration is not in scope"),

        // ---- Arithmetic and lane-independent policy. ----------------------------------------------
        new("AutoTuneLanesIndependent", "drives AutoTuneDescent with explicit values and two separate state objects — the point is that two states do not share, which needs no device"),
        new("AutoTuneStaleStateGuards", "stale-state rules over supplied values, not a live configuration"),
        new("LatencyEstimateComplete", "arithmetic over supplied period values; AuditLatencyUsesDeviceReportedFigures covers the device-figure rules"),

        // ---- Tests that ARE one configuration. ----------------------------------------------------
        new("FanOutToBothOutputs", "this test IS the both-lanes configuration — fan-out to a second output only exists there, so there is nothing to compare against"),
        new("LifecycleChurn", "a churn/soak over start-stop cycles measuring handles and memory; it is about resource behaviour, not about routing"),
    ];

    /// <summary>
    /// The guard itself.
    /// </summary>
    private static string? AudioConfigurationCoverage()
    {
        var root = FindSourceRoot();
        if (root is null)
            return Skip("the source tree is not reachable from here (set REMSOUND_SOURCE_ROOT, as run-tests.ps1 does)");

        var srcDir = Path.Combine(root, "src", "RemSound.App");
        var axis = AudioAxisPattern();
        var coversAll = CoversAllConfigurationsPattern();

        var touching = new List<string>();      // test methods that mention the axis
        var covering = new List<string>();      // ...and demonstrably cover all three
        var methodPattern = TestMethodPattern();

        foreach (var file in Directory.GetFiles(srcDir, "SelfTest*.cs", SearchOption.TopDirectoryOnly))
        {
            // The guard must not audit itself: this file names every exempt method and the axis
            // tokens, so it would match everything and prove nothing.
            if (Path.GetFileName(file).Equals("SelfTest.AudioConfigurations.cs", StringComparison.OrdinalIgnoreCase)) continue;

            var text = File.ReadAllText(file);
            foreach (var body in SplitIntoMethodBodies(text, methodPattern))
            {
                if (!axis.IsMatch(body.Body)) continue;
                touching.Add(body.Name);
                if (coversAll.IsMatch(body.Body)) covering.Add(body.Name);
            }
        }

        Check(touching.Count > 0,
            "the scan found no test touching the WASAPI/ASIO axis at all — the pattern has stopped matching, "
            + "which would make this guard silently useless (the exact failure it exists to prevent)");

        var exempt = AudioAxisExemptions.ToDictionary(e => e.Method, e => e.Reason, StringComparer.Ordinal);
        var unclaimed = touching
            .Where(m => !covering.Contains(m) && !exempt.ContainsKey(m))
            .Distinct()
            .OrderBy(m => m, StringComparer.Ordinal)
            .ToList();

        Check(unclaimed.Count == 0,
            $"{unclaimed.Count} gate test(s) touch the WASAPI/ASIO axis without covering all three audio "
            + $"configurations and without a written exemption: {string.Join(", ", unclaimed)}. "
            + "RemSound has THREE configurations — WASAPI only, ASIO only, and both — decided by which OUTPUT "
            + "DEVICES are ticked, NOT by the audio-mode setting. Loop over AudioConfigurations.All, or add an "
            + "entry to AudioAxisExemptions saying why one configuration is genuinely enough.");

        // Stale exemptions: a reason recorded for a test that no longer touches the axis (or no longer
        // exists) is reassurance about nothing, and it hides the next real gap behind noise.
        var stale = exempt.Keys.Where(m => !touching.Contains(m)).OrderBy(m => m, StringComparer.Ordinal).ToList();
        Check(stale.Count == 0,
            $"{stale.Count} audio-axis exemption(s) name a test that no longer touches the axis or no longer "
            + $"exists: {string.Join(", ", stale)}. Remove them — a list that accumulates dead entries stops "
            + "being read.");

        // And the type itself must still describe three, or every loop above covers the wrong number.
        Check(AudioConfigurations.All.Count == 3, $"there must be exactly three audio configurations, found {AudioConfigurations.All.Count}");
        Check(AudioConfigurations.All.Distinct().Count() == 3, "the three configurations must be distinct");
        Check(Enum.GetValues<AudioConfiguration>().Length == AudioConfigurations.All.Count,
            "every value of the enum must be in the canonical list — a fourth added to one and not the other would give "
            + "every loop above a silent blind spot, which is this guard's own failure mode");

        // The mapping from what is TICKED to which configuration you are in. This is the translation
        // that kept being done by hand and done wrong, so it is pinned rather than trusted.
        Check(AudioConfigurations.From(true, false) == AudioConfiguration.WasapiOnly, "WASAPI outputs only");
        Check(AudioConfigurations.From(false, true) == AudioConfiguration.AsioOnly,
            "ASIO outputs only — the configuration that keeps getting collapsed into the two-lane branch, because an "
            + "ASIO DRIVER is chosen in it and the mode flag therefore says BothIndependent");
        Check(AudioConfigurations.From(true, true) == AudioConfiguration.Both, "both kinds ticked");
        Check(AudioConfigurations.From(false, false) == AudioConfiguration.WasapiOnly,
            "nothing ticked is not a fourth configuration — no ASIO lane exists and nothing is rendering, so it behaves "
            + "as the single-lane WASAPI world");

        // The two questions everything else asks. Getting these wrong silently mis-answers every
        // caller, so they are checked rather than assumed.
        Check(AudioConfiguration.WasapiOnly.UsesWasapi() && !AudioConfiguration.WasapiOnly.UsesAsio(), "WASAPI-only uses one lane");
        Check(AudioConfiguration.AsioOnly.UsesAsio() && !AudioConfiguration.AsioOnly.UsesWasapi(), "ASIO-only uses one lane");
        Check(AudioConfiguration.Both.UsesWasapi() && AudioConfiguration.Both.UsesAsio(), "both uses two");
        Check(AudioConfigurations.All.Count(c => c.HasTwoLanes()) == 1,
            "exactly ONE of the three has two lanes — that is the only case where naming a lane in a label or a figure "
            + "is meaningful rather than noise");
        Check(AudioConfiguration.WasapiOnly.SingleRoute() == RenderRoute.WasapiLane
              && AudioConfiguration.AsioOnly.SingleRoute() == RenderRoute.AsioLane
              && AudioConfiguration.Both.SingleRoute() is null,
            "a single-lane configuration has one route; both-lanes deliberately has NONE, so callers are forced to ask per lane");

        return $"{touching.Count} gate test(s) touch the WASAPI/ASIO axis: {covering.Distinct().Count()} cover all three "
             + $"configurations, {exempt.Count} are exempt with a written reason, 0 unaccounted for";
    }

    /// <summary>Split a source file into test-method bodies by brace matching, so the axis check is
    /// applied to what a method actually DOES rather than to a whole file. A file-level scan would let
    /// one three-configuration test in a file vouch for every other test beside it.</summary>
    private static IEnumerable<(string Name, string Body)> SplitIntoMethodBodies(string text, Regex methodPattern)
    {
        foreach (Match m in methodPattern.Matches(text))
        {
            var name = m.Groups[1].Value;
            var open = text.IndexOf('{', m.Index + m.Length - 1);
            if (open < 0) continue;
            var depth = 0;
            var end = -1;
            for (var i = open; i < text.Length; i++)
            {
                if (text[i] == '{') depth++;
                else if (text[i] == '}')
                {
                    depth--;
                    if (depth == 0) { end = i; break; }
                }
            }
            if (end < 0) continue;
            yield return (name, text[open..end]);
        }
    }

    /// <summary>Matches a gate test method: <c>private static string? SomeTest(</c>. Deliberately
    /// narrow — helpers and pure decision cores are not gate steps and are covered through whichever
    /// step drives them.</summary>
    [GeneratedRegex(@"private static string\? ([A-Za-z_][A-Za-z0-9_]*)\s*\(", RegexOptions.Multiline)]
    private static partial Regex TestMethodPattern();

    /// <summary>Does this method body touch the WASAPI/ASIO axis at all? Any mention of a lane, an
    /// audio mode, an ASIO driver or a per-lane latency figure counts. Deliberately generous: a false
    /// positive costs one line in the exemption list, a false negative costs a bug Ed has to find.</summary>
    [GeneratedRegex(@"WasapiLane|AsioLane|AudioMode\.|AsioDriver|asio|Asio|ASIO|wasapi|Wasapi|WASAPI", RegexOptions.None)]
    private static partial Regex AudioAxisPattern();

    /// <summary>Does this method body demonstrably cover all three? Either it loops over the canonical
    /// list, or it names all three configurations explicitly. Naming them by hand is allowed because
    /// some tests need to assert something DIFFERENT per configuration rather than the same thing
    /// three times — but it must name all three, so a two-way check cannot pass.</summary>
    [GeneratedRegex(@"AudioConfigurations\.All|AudioConfiguration\.WasapiOnly[\s\S]*?AudioConfiguration\.AsioOnly[\s\S]*?AudioConfiguration\.Both|WASAPI only[\s\S]*?ASIO only[\s\S]*?both", RegexOptions.None)]
    private static partial Regex CoversAllConfigurationsPattern();
}
