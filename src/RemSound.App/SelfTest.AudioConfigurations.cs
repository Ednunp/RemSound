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
        new("EveryDialogIsAudited", "names the ASIO loading splash only as a window that is not a dialog; no audio is involved"),
        new("MetronomeAsioWarning", "only a warning's words mention ASIO: it starts and stops the metronome but reads no sound, and the warning is the same whatever outputs are ticked"),
        new("WebListenerStep", "the web listener taps what is SENT, before any output: it sends a WASAPI lane and an ASIO (DAW) lane at once and proves each arrives as its own stream, and which outputs are ticked never reaches it"),
        new("AnAsioInterfaceSwitchedOnLateComesBack", "an ASIO driver's probe: only ASIO only and both have a driver, and the probe is the same for both"),
        new("TheAsioSplashNeverSticks", "the ASIO loading splash is a window shown while a driver opens; no audio is involved"),
        new("AnAsioCopyPlayingKeepsItsSessionAlive", "a second copy to play from exists only with both output types; in WASAPI only and ASIO only the one copy is always the one fed"),
        new("HeadlessWindowsStayOutOfSightUntilHandedOver", "names the ASIO loading splash only to prove it never starts while headless; no audio is involved"),
        // ---- Driver and device lifecycle. About opening and closing hardware, not about which
        // combination a user has selected. --------------------------------------------------------
        new("AsioApartmentThread", "the apartment thread's contract is the same however many lanes exist"),
        new("AuditAsioParkStopsDelivery", "parking is a property of the ASIO capture lane itself; there is no WASAPI equivalent to compare"),
        new("AuditAsioDriverReleaseIsNotRushed", "letting go of a deselected or switched ASIO driver is the ASIO driver's own close; there is no WASAPI equivalent, and no output configuration is involved — it is the capture backend being released"),
        new("AsioOneDriverBothDirections", "one driver instance opened full duplex is a property of the driver and the shared device; there is no WASAPI half, and it runs only on real hardware when a driver is named"),
        new("AuditWakeHoldsTheTunerAndKeepsWhatItLearned", "a wake holds ALL THREE tuned lanes at once — WASAPI, ASIO and the Mixed route a WASAPI-only configuration tunes — and the test asserts every one of them directly, including that each keeps its learned floor. The configurations decide which lanes are live; a wake holds every lane regardless, so the behaviour is identical in all three and is checked on every lane rather than by looping the same wake three times. The restart that follows DOES depend on the configuration, through which lane the main slider drives, and AuditWakeRestartsTheAudioWithTheTuneFromBeforeSleep loops all three"),
        new("AuditOutputReopenIsNotJitter", "this is about output KIND, not configuration: a WASAPI output fault holds the WASAPI lane and the Mixed route (what a WASAPI-only configuration tunes), a missing ASIO output holds the ASIO lane alone, and the test asserts each mapping and that the other kind is left untouched. That covers every lane any configuration can have; looping configurations would repeat the same fault on the same lanes"),
        new("AuditPostWakeBurstSplitsNetworkFromRender", "a log cadence and format check: the burst and the network/render gap split are written the same way whichever outputs are ticked, and a lane that does not exist prints a dash. The test formats both a both-lanes line and a WASAPI-only line; driving the timer in each configuration would exercise the same code three times"),
        new("AuditReturningOutputForgetsItsOldFloor","one lane's tune memory reacting to its own output leaving and returning. Both lanes use the same memory type, so the test's WASAPI and ASIO passes run the same code under two names; the configuration decides which lanes exist, not what a lane's memory does when its device comes back"),
        new("AuditCaptureSurvivesADeadDeviceEvent", "this is WASAPI loopback CAPTURE, and the ASIO lane cannot reach this code at all — ASIO capture runs on its own driver callback with no WASAPI event anywhere in it, so there is nothing to compare. The three configurations are decided by which OUTPUT devices are ticked and have no bearing whatever on how a capture device is polled: the same loop would run identically in all three"),
        new("AuditNothingCreepsOverHours", "the three things soaked here — the WASAPI cushion target, the drift loop and the tuner's learned floor — are driven directly as arithmetic, and the WASAPI output stage has NO ASIO counterpart at all: ASIO pulls straight from the engine, with no device buffer, no cushion target and no rate-trimming resampler. That asymmetry is the SUBJECT of the test (the fault appears on WASAPI and never on ASIO), not a gap in it. Running the same arithmetic three times would add nothing"),
        new("AuditLongRunReportIsActuallyWritten", "this is a WIRING check, not a behaviour one: it drives the real per-second tick with NOTHING running — no receiver, no sender, no device ticked — because that is precisely the state in which the per-second diagnostic line goes silent and produced five lines in fourteen hours. There is no audio configuration in that state to vary. What the line SAYS in each of the three is covered by AuditLongRunReportSaysWhatCreeps, which loops all three"),
        new("AuditUntickedLaneIsNotFedForever", "this IS the both-lanes configuration and cannot be anything else — it is about switching one of two lanes off, and WASAPI-only and ASIO-only have no second lane to switch off. With one active lane no mirror replica is ever made, so there is nothing that can be left written-but-unread: those two configurations cannot REACH this bug rather than being untested for it. The test does run both ways round, ASIO off with WASAPI surviving and WASAPI off with ASIO surviving, because nothing in the read path is lane-specific"),
        new("APluginTrackPlaysWhenTheWasapiOutputStops", "this IS the both-outputs configuration and cannot be anything else: it is about the WASAPI output stopping while the ASIO one plays on, and with one kind of output there is no second copy to fall back to - the primary is the only copy and is always the one fed"),
        new("AFailedAsioOpenIsTriedAgain", "the ASIO backends' own open-and-retry: there is no WASAPI half to it, and no output configuration is involved - it drives the two backends directly with a driver that cannot open"),
        new("AuditPluginShapingSwitchActuallySwitches", "a claimed peer belongs to a DAW TRACK, which has no output lane — ReadClaimedPeer deliberately does not filter by lane, and the shaping runs inside the peer's own session read before any lane is involved. The three configurations decide which SPEAKER lanes exist, and a claimed peer is excluded from every one of them"),

        // ---- Pure arithmetic and decision cores, tested by feeding values directly. Covering three
        // configurations would mean feeding the same function the same numbers three times. --------
        new("AuditLatencyUsesDeviceReportedFigures", "pure arithmetic over supplied values — the configuration is the caller's business, and AuditOutputLatencyIsReportedPerLane covers the picking"),
        new("AuditAudioLoopsAreNotAsync", "a reflection check on two methods' compiler attributes; no audio runs at all"),
        new("AuditWakePutsBackTheSettledTuneNotTheLastMoment", "pure arithmetic over a supplied history — which samples the median and the floors are taken from. It names WASAPI and ASIO only because those are the two sliders it reads; which lane each slider drives in each configuration is AuditWakeRestartsTheAudioWithTheTuneFromBeforeSleep's job, and that loops all three"),

        // ---- Plugin. One instance is one track and one peer; the DAW is the device, so the machine's
        // own output configuration is not part of what is being tested. ---------------------------
        new("PluginBridgeEndToEnd", "the bridge carries raw float between app and DAW; the machine's output configuration is not in that path. It reads the speakers once, in WASAPI only, as its premise; a claimed peer leaving the speakers in all three configurations is PluginSurvivesSessionChurn"),
        new("PluginRateConversion", "resampler arithmetic between host rate and the 48k wire; no lane involved"),
        // PluginDoubleAudioGuard is NOT exempt: the doubling bug it now also guards against was
        // configuration-specific (it only bit with both kinds of output ticked), so it covers three.

        // ---- Settings, profiles and lists. Persistence and list semantics are identical whatever is
        // ticked; the ticked state is DATA these round-trip, not behaviour that varies. -------------
        new("ProfileRoundTrip", "settings persistence — the audio mode is a value being saved, not behaviour under test"),
        new("SettingsRoundTrip", "save/load arithmetic over the settings store, and the clamp-symmetry sweep already covers BOTH lanes' jitter-buffer settings by name — what is ticked changes none of it"),
        new("V5ConfigRoundTrip", "serialisation and the DEFAULTS a blank profile carries, including both lanes' jitter buffers by name — a default is a stored number, not behaviour that varies with what is ticked"),
        new("MainWindowProfileRoundTrip", "the window's save/reload path; it names the WASAPI send mode (applications) only as a value to round-trip, and no output configuration is involved"),
        new("ProfileDriftTripwire", "reflects over every settings property to catch one that stops persisting"),
        new("AsioTickMemory", "per-driver tick memory is about remembering a selection, not about rendering it"),
        new("SendAppListSemantics", "the active/remembered app lists are WASAPI-only by construction"),
        new("AppSendEnumeration", "enumerates audio sessions; per-application capture exists only on the WASAPI path"),
        new("DefaultOutputFollower", "the 'follow the Windows default' entry is a WASAPI device concept — ASIO has no default"),
        new("SessionStartWatcher", "it watches Windows audio SESSIONS on the default RENDER endpoint, which is a WASAPI-only notion — an ASIO driver creates no session for it to see, so there is no second or third configuration to compare against"),

        // ---- Service. Send-only, WASAPI-only, and it never renders — so it has no output
        // configuration at all. ---------------------------------------------------------------------
        new("ServiceProfileIsolation", "the service profile is a separate store; the service never renders"),
        new("ServiceSendHostStream", "the service is WASAPI send-only and has no output lane"),
        new("ServiceLooksNamesUpAgainUntilTheyAnswer", "the service is WASAPI send-only and has no output lane; this is about name look-ups"),
        // Seen since the pattern also counts rendering through the engine and a route tag (2026-09-28):
        new("ARecordingThatDiesTellsThePerson", "your own send, fed through the sender's recording tap to a writer made to fail; which outputs are ticked never enters it - the recorder in every configuration is RecordingCapturesInEveryConfiguration"),
        new("RecordingReportsItsOwnDeath", "the recording writer's death, fed through the sender's and the per-peer taps directly; the route only labels the block, and which outputs are ticked never enters it"),
        new("RecordingMultiPeerSplit", "one file per person from the per-peer tap, fed directly; the route only labels the block - which lanes render into the tap is RecordingCapturesInEveryConfiguration"),
        new("AuditWavStopsAtTheFormatLimit", "the WAV writer's 4 GB limit; the route only labels the block handed to it"),
        new("TidyTheAsioOutputNeverThrowsIntoTheDriver", "the ASIO output's own read guard, fed a source that throws, directly - which outputs are ticked never enters it; the WASAPI output has no driver callback to protect"),
        // Push to talk (2026-09-28): the simulator steps run WASAPI only, ASIO only and both through SimConfig, as the other
        // simulator steps do; the rest never reach the outputs.
        new("PushToTalkHoldsOnlyItsPath", "it runs WASAPI only, ASIO only and both through SimConfig - in both, each lane carries a tone of its own, heard apart on both outputs"),
        new("PushToTalkOffChangesNothing", "it runs WASAPI only, ASIO only and both through SimConfig and compares every output sample for sample"),
        new("PushToTalkRecordsAsHeard", "it runs WASAPI only, ASIO only and both through SimConfig, reading the recording tap of the lane each sends on"),
        new("PushToTalkInTheProfile", "push to talk's settings in the profile and its key on this computer; which outputs are ticked never enters it"),
        new("PushToTalkIsNotInTheService", "the service is WASAPI send-only and has no output lane; this is that it ignores push to talk"),
        new("MetronomeIsNotInTheService", "the service is WASAPI send-only and has no output lane to click on; this is that it reads no metronome message"),
        new("PushToTalkWindowSavesToTheProfile", "the push to talk window and what OK keeps; the gate it sets is proved in all three configurations by the PUSH TO TALK simulator steps"),
        new("SendAppCaptureChangeDetection", "capturing one application's sound is WASAPI's process loopback - there is no ASIO form of it, and which outputs are ticked never enters it"),
        new("SharedAsioDeviceIsSharedAndLetGoByTheLast", "the register of shared ASIO drivers - one instance per name, held and let go - which knows nothing of which outputs are ticked; both directions on one real driver is AsioOneDriverBothDirections, when a driver is named"),
        new("ASenderThatStartsCountingAgainKeepsPlaying", "a session's own sequence bookkeeping as packets arrive, before any output lane; the fan-out to lanes comes after and is covered by the lane steps"),
        new("ASessionIsNeverFreedWhileItDecodes", "a race between a session's decode and its pruning, on the receive thread, before any output lane"),
        new("OneStreamFromTwoAddressesIsOneSession", "which session a packet joins, decided by person and stream before any lane; the per-lane copies made after are the plugin and lane steps' business"),
        new("ServiceSilentCaptureSelfHeal", "the service is WASAPI send-only by design (it runs with nobody signed in, where ASIO drivers do not) and has no output lane; this is its capture self-heal"),
        new("ServiceBootEar", "the service is WASAPI send-only by design (it runs with nobody signed in, where ASIO drivers do not) and has no output lane; this is which capture it opens at boot"),
        new("ServiceFollowsAServerThatMoves", "the service is WASAPI send-only and has no output lane; this is about looking a server's name up again and joining its group where it now is"),
        new("TheServiceDropsAndRearmsAPeer", "the service is WASAPI send-only and has no output lane; this is which peers its sender sends to, judged by the heartbeat, and it names WASAPI only to pick the device the service captures"),
        // The simulator (2026-09-26): each step sets up the configuration it is about through SimConfig, which builds the
        // receiver's lanes exactly as ticking output devices does (SetActiveOutputLanes); THREE CONFIGURATIONS runs all three.
        new("SimulatorThreeConfigurationsPlayClean", "it runs WASAPI only, ASIO only and both, through SimConfig - the simulator sets "
            + "the receiver's output lanes the way ticked devices do, not through the audio-mode setting"),
        new("SimulatorAsioLinkIsClean", "about the ASIO-at-both-ends link and its three ways of sending, two of which exist only on the "
            + "ASIO lane; the three configurations are compared in SimulatorThreeConfigurationsPlayClean"),
        new("SimulatorHearsATooSmallBuffer", "about a buffer too small for the network, which is the same arithmetic on any output; "
            + "run on ASIO, where a too-small buffer is most likely to be tried"),
        new("SimulatorHoldsTwoDriftingCards", "it runs WASAPI only, ASIO only and both, through SimConfig - the simulator sets up "
            + "its own receiving end rather than ticking devices through AudioConfigurations"),
        new("SimulatorDescentKeepsTheHousePace", "the descent is each output's own drain: it listens to the ASIO output and the WASAPI "
            + "one; with both kinds ticked each drains the same way (the three are compared in SimulatorThreeConfigurationsPlayClean)"),
        new("SimulatorEverydaySetupHolds", "it listens to WASAPI only on purpose - the everyday set-up - and to PCM on both kinds of "
            + "output; the three configurations are compared in SimulatorThreeConfigurationsPlayClean"),
        new("SimulatorDropoutTunerFindsTheEdge", "the rare-dropout auto-tune is for the ASIO output only"),
        // 2026-09-28: these run WASAPI only, ASIO only and both through SimConfig - the simulator builds the receiver's lanes
        // the way ticked devices do - so the guard's pattern (AudioConfiguration) cannot see that they cover all three.
        new("SimulatorSnapsDownAndDrains", "it runs WASAPI only, ASIO only and both through SimConfig, from 500, 200 and 100 ms, and "
            + "asserts inside every one; the hand-moved slider is tried on WASAPI only and ASIO only, the two ways a lane is set by hand"),
        new("SimulatorAutoTuneComesDownFrom500", "it runs WASAPI only, ASIO only and both through SimConfig, each lane the main window "
            + "tunes in that configuration (the Mixed route, the ASIO lane, or both lanes), and asserts inside every one"),
        new("SimulatorAutoTuneClimbsFrom5", "it runs WASAPI only, ASIO only and both through SimConfig, each lane the main window tunes "
            + "in that configuration, and asserts inside every one"),
        new("SimulatorBothOutputsAreEachListenedTo", "this IS the both-outputs configuration: it listens to the WASAPI output beside the "
            + "ASIO one and stalls one to watch the other; with one kind of output there is no second output to listen to or to stall"),
        new("SimulatorLatencyReadoutMatchesTheTrueDelay", "it runs WASAPI only, ASIO only and both through SimConfig, reading each lane's "
            + "line of the readout as FormatMeasuredLatency writes it for that configuration"),
        new("SimulatorStepProbeFlagsAPlantedClick", "it runs WASAPI only, ASIO only and both through SimConfig; the probe itself sits at "
            + "decode, before any output"),
        new("SimulatorHoldsOvernight", "the overnight slowdown it soaks for happens on WASAPI and never on ASIO, and the WASAPI output "
            + "stage it puts in the path has no ASIO counterpart at all (ASIO pulls straight from the engine) - that asymmetry is the "
            + "subject; the drift and stall steps cover the engine side in all three configurations"),
        new("SimulatorRecoversFromAStall", "it runs WASAPI only, ASIO only and both through SimConfig, each stall in each, and listens "
            + "to both outputs of both"),
        new("SimulatorWasapiOutputStageHoldsItsCushion", "the WASAPI output stage exists only in front of a WASAPI output, so it runs "
            + "the two configurations that have one - WASAPI only and both - through SimConfig; ASIO only has no stage at all (ASIO pulls "
            + "straight from the engine), and the engine side in all three is SimulatorLanesStayApart's and the drift and stall steps'"),
        new("SimulatorServiceStream", "it runs the service's stream into a listener on WASAPI only, ASIO only and both, through SimConfig - "
            + "the sender is the service's kind (WASAPI only, one untagged stream) in every one, as the service can be nothing else"),
        new("SimulatorLanesStayApart", "it runs WASAPI only, ASIO only and both through SimConfig for every copy's clock reading, asserting "
            + "inside every one; the sliders' coupling can only exist with both, where each lane has a slider and a copy of every stream"),
        new("SimulatorOutputHiccupIsNotAClock", "it runs ASIO only, both and WASAPI only through SimConfig - an ASIO output stopping on ASIO "
            + "only and both, a WASAPI card stopping behind the output stage on WASAPI only and both - and asserts on every run"),
        // The simulator review's improvements and the fixes they found (2026-09-30).
        new("InputStopsAndComesBack", "it runs WASAPI only (an input device that stops), ASIO only and both (the sending card stops) "
            + "through SimConfig, asserting in each; the momentary empties that must never be refilled run in ASIO only and WASAPI "
            + "only, the refill being SessionPlayout's, the same on either output"),
        new("PcmBlocksGoOutInEqualPieces", "the pieces are the sender's, checked on both lanes on the wire; listened to, it runs WASAPI "
            + "only, ASIO only and both through SimConfig, asserting in each"),
        new("UnderrunNoiseBurstKeepsEachChannelsLevel", "it runs ASIO only, WASAPI only and both through SimConfig, asserting in each"),
        new("SimulatorDriftAfterAStall", "it runs ASIO only, both and WASAPI only through SimConfig, every stall time in each"),
        new("SimulatorAgreesWithLiveRecordings", "a recording is replayed in the configuration it names - the seed's is ASIO only - so "
            + "the configuration is the recording's and cannot be anything else; the receive path in all three is "
            + "SimulatorRecoversFromAStall's and SimulatorLanesStayApart's"),
        new("SimulatorBurstsAndQueues", "loss in bursts and a queue that builds are the network's, met per stream before any output; it "
            + "runs ASIO only, where a small buffer shows them soonest; the receive path in all three is SimulatorRecoversFromAStall's "
            + "and SimulatorLanesStayApart's"),
        new("SimulatorPushModeRates", "push mode is the WASAPI input's own converter - an ASIO input is opened at 48 kHz and has none - "
            + "so WASAPI only is the one configuration it exists in and cannot be anything else"),
        new("SimulatorSampleRates", "the converters it checks are the capture side's, the mixing engine's and push mode's, fed by WASAPI "
            + "inputs - ASIO is opened at 48 kHz - so WASAPI only is the one configuration they exist in and cannot be anything else"),
        new("SimulatorReplaysARecordedNetwork", "a replay is the network's arrivals, before any output; it checks that the configuration a "
            + "recording names (WASAPI only, ASIO only or both) is read back, and replays in ASIO only; the receive path in all three is "
            + "SimulatorRecoversFromAStall's and SimulatorLanesStayApart's"),
        new("SimulatorServerHop", "the server hop is between the senders and the receiver, over the network and through the server, met per "
            + "stream before any output; it runs ASIO only; the receive path in all three is SimulatorRecoversFromAStall's and "
            + "SimulatorLanesStayApart's"),
        new("SimulatorWallClock", "the wall-clock mode plays one output per run and refuses both at once (checked here); it runs WASAPI "
            + "only and ASIO only setups, each against virtual time, and virtual time's both is SimulatorLanesStayApart's"),
        new("TheNetworkRecorderKeepsTheLastMinute", "the recorder keeps packet arrivals as they reach the receiver, before any output: "
            + "what is ticked changes nothing it keeps"),
        new("CrossPortKnownAnswers", "the bytes on the wire are the same whichever outputs are ticked; the lane byte's own values are pinned directly"),
        new("SimulatorRepeatsItself", "repeatability is a property of the simulator's clock and dice, the same whichever output plays"),
        new("TheHeartbeatCarriesTheJamulusFlag", "the heartbeat is per connection, not per output; the flag only says this copy plays "
            + "through ASIO and can decode Jamulus-style Opus - which configurations do is JAMULUS-STYLE AUTOMATIC's, which walks all three"),
        new("SimulatorReorderWaitSlotsPacketsBackIn", "the reorder wait is per stream, before any output is involved"),
        new("SimulatorLatePacketsAreDropped", "late and doubled packets are handled per stream before any output is involved"),
        new("SimulatorPerStreamSwitchCutsTheDelay", "the per-stream switch only matters with both kinds of output, where a smooth "
            + "ASIO stream and a lumpy WASAPI one share an output - that is the configuration it runs"),
        new("SimulatorRepairsLostPackets", "loss repair happens per stream at decode, before any output; Jamulus-style frames exist "
            + "only on the ASIO lane"),
        // The latency lab (2026-09-26).
        new("AsioBufferSizeIsFittedToTheDriver", "the ASIO buffer size is an ASIO-only setting, and this is the arithmetic of fitting it "
            + "to a driver's rules plus a check on NAudio's layout; no configuration is running, and the gate has no ASIO driver"),
        new("LatencyLabRetiredSwitchesStillLoad", "a global config file read and written: the retired names include ASIO ones, but "
            + "nothing here runs audio, and reading a file is the same in every configuration"),
        new("TheAsioBufferSizeIsAProfileSetting", "the ASIO buffer size is an ASIO setting: a WASAPI device in shared mode has no buffer "
            + "for a program to choose, so WASAPI only has nothing to vary; the window it drives has a (stand-in) ASIO driver, and ASIO "
            + "only and 'both' ask the one driver the same thing at its open, which is what is checked"),
        new("PerStreamBuffersFollowTheirOwnArrivals", "a decode-only receiver with no output device: a stream's own sizing is the same on "
            + "any output, and the test's two streams are one peer's ASIO and WASAPI streams"),
        // The jitter buffer boxes (2026-10-02).
        new("JitterBoxSelectsAfterEnter", "the two number boxes themselves, the jitter buffer box and the ASIO one, and what Enter does to "
            + "their text; both are driven, and no sound runs, so there is no configuration to vary"),
        new("TheAsioLaneSendsEachStreamToTheRightPeople", "the Jamulus-style stream exists only on the ASIO lane, driven here directly; "
            + "PCM's packet per callback is checked on the ASIO lane and the WASAPI lane beside it, the two lanes of 'both'; in "
            + "WASAPI-only there is no ASIO lane to act on"),
        new("MetronomeThroughTheServer", "two windows on one machine, each fed on its WASAPI lane, through the real relay: what is "
            + "checked is the server's timing, the same whichever lane is timed; which lane is timed, and with what input "
            + "delay, is checked in all three configurations by MetronomeCaptureTimesForTheServer"),
        new("ServerStatusThroughTheServer", "what is checked is what the server says and its ping coming back - counts, a version, "
            + "a member's own packets - decided by the server, not by any output; the apps only decode, no output configuration "
            + "is opened"),
        new("HearMyselfThroughTheServer", "what is checked is the server sending our own streams back, and the relay group letting "
            + "them in, decided by the server and the ticks, not by the outputs; the receivers only decode - no output configuration "
            + "is opened - and an echo then plays as any peer's stream does, which the receive suites check in all three"),
        new("JamulusStyleThroughTheServer", "the Jamulus-style stream exists only on the ASIO lane, driven here directly; what is "
            + "checked is who the server may pass it to, a sending rule decided per target, and the receivers only decode - no "
            + "output configuration is opened"),
        new("AFailedSendSourceIsTriedAgain", "the heal is in the WASAPI side of the capture, the same code with or without ASIO beside it; "
            + "'both' needs a real ASIO driver, which the gate does not have"),
        new("ADeadSendSourceIsReopenedAlone", "the heal is in the WASAPI side of the capture, the same code with or without ASIO beside it; "
            + "the step also runs the sender with an ASIO lane beside the WASAPI sources (a stand-in driver) and proves the ASIO stream carries on"),
        new("AuditPushModeReadsIntegerCapture", "pure sample-format arithmetic on the push-mode capture class, fed bytes directly; it names the class, and no output configuration or audio device is involved"),
        new("AuditServiceSendsAnApplicationStartedLater", "it names the WASAPI send mode only to choose specific applications; the service never opens ASIO and never renders, so there is no output configuration to vary"),
        new("ServiceSenderParity", "compares service and app SENDER settings; the receive configuration is not in scope"),

        // ---- Arithmetic and lane-independent policy. ----------------------------------------------
        new("AutoTuneStaleStateGuards", "one headless window driving each route's baseline and evidence rules directly — Mixed, the WASAPI lane and the ASIO lane, every route any configuration can tune; what is ticked does not change those rules"),
        new("LatencyEstimateComplete", "arithmetic over supplied period values; AuditLatencyUsesDeviceReportedFigures covers the device-figure rules"),

        // ---- Tests that ARE one configuration. ----------------------------------------------------
        new("FanOutToBothOutputs", "this test IS the both-lanes configuration — fan-out to a second output only exists there, so there is nothing to compare against"),
        new("LifecycleChurn", "a churn over start-stop cycles measuring handles; it changes the audio mode (opening a real ASIO driver only with REMSOUND_TEST_ASIO) as one more resource transition, not to check routing, and it opens no output"),
        new("RunControlSuite", "runs once per configuration: its registration loops SuiteConfigs, which names all three (WASAPI only, ASIO only, both), and each run proves its configuration took before auditing anything"),

        // ---- Steps that return plain string, unseen by this guard until 2026-09-13. The first four are
        // SENDER side: the lane byte a sender stamps follows the sender's audio mode, and which outputs are
        // ticked is the receiver's business, so the three receiving configurations do not apply. -----------
        new("PluginLaneNeverCollides", "sender side: the plugin lane's tag follows the sender's audio mode, and the step drives both modes a sender can be in"),
        new("PluginLaneFollowsFormatChanges", "sender side: a mode change is one of the triggers that must rotate the plugin lane's stream id, alongside codec, rate and arming — not a configuration under test"),
        new("PluginSendsWithNoCaptureDevice", "sender side, with nothing ticked in the app: that one state is the case under test, and the tag it reads is the sender's lane, not an output"),
        new("PluginLaneObeysTheHouseRules", "sender side: no password, mute and the recorder tap are the same SenderLane code in every sender mode; the route it reads is the plugin lane's own tag"),
        new("ThreeConcurrentStreamsFromOneSender", "about session identity on the receiver: three streams from one peer, one per lane tag, stay three sessions and a shared tag still supersedes; decode only, so no output is opened"),
        new("AuditJitterBufferHasOneName", "reads the source for one stale name and anchors on the WASAPI-only label; the wording each configuration shows is set in UpdateBothIndependentVisibility, which the control suite runs in all three"),

        // ---- The control channel (--headless / --control), 2026-09-24. ----------------------------------------------
        new("RemoteControlDrivesTheWholeApp", "about reaching controls by the names a screen reader reads: the ASIO auto-tune box is named only to prove that two controls sharing a start are refused, never guessed. The channel presses the same controls through the same handlers whichever configuration is ticked, and what each control does in each of the three is the control suite's, which runs all three"),
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
        var readsSpeakers = new HashSet<string>(StringComparer.Ordinal);   // ...and read what the speakers play
        var covering = new List<string>();      // ...and demonstrably cover all three
        var decorative = new List<string>();    // ...whose loop contains no assertion at all
        var split = new Dictionary<string, (int Inside, int Outside)>(StringComparer.Ordinal);
        var methodPattern = TestMethodPattern();
        var registered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.GetFiles(srcDir, "SelfTest*.cs", SearchOption.TopDirectoryOnly))
            foreach (Match m in RegisteredStepPattern().Matches(File.ReadAllText(file)))
                registered.Add(m.Groups[1].Value);
        Check(registered.Count > 100,
            $"the scan found only {registered.Count} registered steps — the RunStep pattern has stopped matching, and this guard "
            + "would audit almost nothing");

        foreach (var file in Directory.GetFiles(srcDir, "SelfTest*.cs", SearchOption.TopDirectoryOnly))
        {
            // The guard must not audit itself: this file names every exempt method and the axis
            // tokens, so it would match everything and prove nothing.
            if (Path.GetFileName(file).Equals("SelfTest.AudioConfigurations.cs", StringComparison.OrdinalIgnoreCase)) continue;

            var text = File.ReadAllText(file);
            foreach (var body in SplitIntoMethodBodies(text, methodPattern))
            {
                if (!body.Nullable && !registered.Contains(body.Name)) continue;
                if (!axis.IsMatch(body.Body)) continue;
                touching.Add(body.Name);
                if (SpeakerReadPattern().IsMatch(CodeOnly(body.Body))) readsSpeakers.Add(body.Name);
                // Judged on the CODE: a comment or a message that says "WASAPI only, ASIO only and both", or names
                // AudioConfigurations.All, is not a loop over them (2026-09-24).
                var code = CodeOnly(body.Body);
                if (!coversAll.IsMatch(code)) continue;
                covering.Add(body.Name);

                // AND HOW MUCH OF IT ACTUALLY RUNS IN THERE.
                //
                // Containing a three-configuration loop was the whole test, and it is not enough: once
                // a method had one anywhere in its body it counted as covering FOREVER, and every
                // assertion added afterwards — which get appended at the end, always — sat outside it
                // unnoticed. That is exactly how the measured-latency readout came to have its new
                // arithmetic checked in one shape only while this guard stayed green (Ed, 2026-08-24,
                // and not for the first time). A guard that checks a shape exists rather than what it
                // covers is the disease, not the cure.
                var (inside, outside) = CountAssertionsInsideConfigurationLoop(code);
                split[body.Name] = (inside, outside);
                if (inside == 0) decorative.Add(body.Name);
            }
        }

        Check(touching.Count > 0,
            "the scan found no test touching the WASAPI/ASIO axis at all — the pattern has stopped matching, "
            + "which would make this guard silently useless (the exact failure it exists to prevent)");

        var exempt = AudioAxisExemptions.ToDictionary(e => e.Method, e => e.Reason, StringComparer.Ordinal);
        // An exemption says one configuration is enough; a test that reads what the SPEAKERS play is exactly where the
        // three differ (the mirror copies live there). PluginSurvivesSessionChurn read the speaker mix under an exemption
        // saying it was "not a rendering path" (found 2026-09-28). Such an exemption stands only when the test is one
        // configuration by its nature ("cannot be anything else"), or names the test that covers the speakers in all three.
        var exemptButListening = exempt
            .Where(e => readsSpeakers.Contains(e.Key)
                        && !e.Value.Contains("cannot be anything else", StringComparison.Ordinal)
                        && !covering.Any(c => e.Value.Contains(c, StringComparison.Ordinal)))
            .Select(e => e.Key).OrderBy(m => m, StringComparer.Ordinal).ToList();
        Check(exemptButListening.Count == 0,
            $"{exemptButListening.Count} exempt test(s) read what the speakers play, which is where the three configurations differ, "
            + $"and neither are one configuration by nature nor name the test that covers all three: {string.Join(", ", exemptButListening)}. "
            + "Loop over AudioConfigurations.All instead of excusing them.");
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

        // A DECORATIVE LOOP is worse than none: it satisfies the check above while asserting nothing.
        Check(decorative.Count == 0,
            $"{decorative.Count} test(s) loop over the three configurations but make no assertion inside the loop, so the "
            + $"loop is decoration that satisfies this guard and proves nothing: {string.Join(", ", decorative)}");

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

        // REPORT THE SPLIT, every run. A method whose assertions have drifted outside its loop is the
        // failure this guard could not see, so the numbers are printed rather than left to be
        // discovered: a test showing 3 inside and 40 outside is telling you where the next gap is.
        var lopsided = split.Where(kv => kv.Value.Outside > kv.Value.Inside)
            .OrderByDescending(kv => kv.Value.Outside - kv.Value.Inside)
            .Select(kv => $"{kv.Key} ({kv.Value.Inside} in / {kv.Value.Outside} out)")
            .ToList();

        return $"{touching.Count} gate test(s) touch the WASAPI/ASIO axis: {covering.Distinct().Count()} cover all three "
             + $"configurations, {exempt.Count} are exempt with a written reason, 0 unaccounted for"
             + (lopsided.Count == 0
                 ? "; every covering test asserts more inside its three-configuration loop than outside it"
                 : $"; MORE ASSERTIONS OUTSIDE THE LOOP THAN INSIDE in {lopsided.Count}: {string.Join(", ", lopsided)}");
    }

    /// <summary>How many assertions in this method body sit INSIDE a three-configuration loop, and how
    /// many outside it. Brace-matched from each <c>AudioConfigurations.All</c> loop.
    ///
    /// <para>The number that matters is the second one. A method keeps its "covers all three" mark for
    /// as long as it contains a loop, so assertions appended after it — and they are always appended
    /// after it — accumulate outside, unseen.</para></summary>
    private static (int Inside, int Outside) CountAssertionsInsideConfigurationLoop(string body)
    {
        var total = CountOccurrences(body, "Check(") + CountOccurrences(body, "Require(");
        var inside = 0;
        for (var i = body.IndexOf("AudioConfigurations.All", StringComparison.Ordinal); i >= 0;
             i = body.IndexOf("AudioConfigurations.All", i + 1, StringComparison.Ordinal))
        {
            var open = body.IndexOf('{', i);
            if (open < 0) continue;
            var depth = 0;
            var close = -1;
            for (var j = open; j < body.Length; j++)
            {
                if (body[j] == '{') depth++;
                else if (body[j] == '}' && --depth == 0) { close = j; break; }
            }
            if (close < 0) continue;
            var block = body[open..close];
            inside += CountOccurrences(block, "Check(") + CountOccurrences(block, "Require(");
            // Skip past this loop so a nested AudioConfigurations.All is not counted twice.
            i = close;
        }
        return (inside, Math.Max(0, total - inside));
    }

    /// <summary>The source with its comments and the insides of its string and character literals taken out, so a
    /// pattern matches what the code DOES rather than what it says about itself. Line breaks are kept.</summary>
    internal static string CodeOnly(string source)
    {
        var sb = new System.Text.StringBuilder(source.Length);
        var i = 0;
        while (i < source.Length)
        {
            var c = source[i];
            var next = i + 1 < source.Length ? source[i + 1] : '\0';
            if (c == '/' && next == '/')
            {
                while (i < source.Length && source[i] != '\n') i++;
                continue;
            }
            if (c == '/' && next == '*')
            {
                var end = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                var stop = end < 0 ? source.Length : end + 2;
                for (var k = i; k < stop; k++) if (source[k] == '\n') sb.Append('\n');
                i = stop;
                continue;
            }
            // A string: up to two of $ and @ in either order, then the quote.
            var j = i;
            var verbatim = false;
            while (j < source.Length && j - i < 2 && (source[j] == '$' || source[j] == '@')) { verbatim |= source[j] == '@'; j++; }
            if (j < source.Length && source[j] == '"')
            {
                j++;
                while (j < source.Length)
                {
                    if (verbatim && source[j] == '"' && j + 1 < source.Length && source[j + 1] == '"') { j += 2; continue; }
                    if (!verbatim && source[j] == '\\') { j += 2; continue; }
                    if (source[j] == '"' || (!verbatim && source[j] == '\n')) break;
                    j++;
                }
                sb.Append("\"\"");
                i = Math.Min(source.Length, j + 1);
                continue;
            }
            if (c == '\'')
            {
                var k = i + 1;
                k += k < source.Length && source[k] == '\\' ? 2 : 1;
                while (k < source.Length && source[k] != '\'' && k - i < 10) k++;
                sb.Append("''");
                i = Math.Min(source.Length, k + 1);
                continue;
            }
            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }

    /// <summary>Split a source file into test-method bodies by brace matching, so the axis check is
    /// applied to what a method actually DOES rather than to a whole file. A file-level scan would let
    /// one three-configuration test in a file vouch for every other test beside it.</summary>
    private static IEnumerable<(string Name, bool Nullable, string Body)> SplitIntoMethodBodies(string text, Regex methodPattern)
    {
        foreach (Match m in methodPattern.Matches(text))
        {
            var name = m.Groups["name"].Value;
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
            yield return (name, m.Groups["nullable"].Success, text[open..end]);
        }
    }

    /// <summary>Matches a method that could be a gate step: <c>private static string? SomeTest(</c> or
    /// <c>private static string SomeTest(</c>. A <c>string?</c> method is audited as it always was; a plain
    /// <c>string</c> one only when RunStep registers it (see <see cref="RegisteredStepPattern"/>), because most
    /// of those are helpers that build text. Until 2026-09-13 the pattern took <c>string?</c> only, and the
    /// registered steps that return plain <c>string</c> were never seen by this guard.</summary>
    [GeneratedRegex(@"private static string(?<nullable>\?)? (?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\(", RegexOptions.Multiline)]
    private static partial Regex TestMethodPattern();

    /// <summary>A registered step: the method a <c>RunStep(results, "...", Method)</c> line names, directly or
    /// through <c>() =&gt; Method(...)</c>.</summary>
    [GeneratedRegex(@"RunStep\(results,\s*\$?""[^""]*"",\s*(?:\(\)\s*=>\s*)?([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Multiline)]
    private static partial Regex RegisteredStepPattern();

    /// <summary>Does this method body touch the WASAPI/ASIO axis at all? Any mention of a lane, an
    /// audio mode, an ASIO driver or a per-lane latency figure counts. Deliberately generous: a false
    /// positive costs one line in the exemption list, a false negative costs a bug Ed has to find. Since 2026-09-28 it also
    /// counts rendering through the engine and its helpers without naming a lane (PlayoutEngine, SetLaneActive, a route,
    /// PeakOfMix, NewEngineFor, SpeakerPeak) - a test that did that was invisible to this guard.</summary>
    [GeneratedRegex(@"WasapiLane|AsioLane|AudioMode\.|AsioDriver|asio|Asio|ASIO|wasapi|Wasapi|WASAPI|new PlayoutEngine\(|SetLaneActive\(|RenderRoute\.Mixed|ReadForRoute\(|PeakOfMix\(|NewEngineFor\(|SpeakerPeak\(", RegexOptions.None)]
    private static partial Regex AudioAxisPattern();

    /// <summary>Reading what the speakers play: the mix, a lane's own output, or the helper that reads them.</summary>
    [GeneratedRegex(@"PeakOfMix\(|LaneOutput\.Read\(|SpeakerPeak\(", RegexOptions.None)]
    private static partial Regex SpeakerReadPattern();

    /// <summary>Does this method body demonstrably cover all three? Either it loops over the canonical
    /// list, or it names all three configurations explicitly. Naming them by hand is allowed because
    /// some tests need to assert something DIFFERENT per configuration rather than the same thing
    /// three times — but it must name all three, so a two-way check cannot pass.</summary>
    [GeneratedRegex(@"AudioConfigurations\.All|AudioConfiguration\.WasapiOnly[\s\S]*?AudioConfiguration\.AsioOnly[\s\S]*?AudioConfiguration\.Both|WASAPI only[\s\S]*?ASIO only[\s\S]*?both", RegexOptions.None)]
    private static partial Regex CoversAllConfigurationsPattern();
}
