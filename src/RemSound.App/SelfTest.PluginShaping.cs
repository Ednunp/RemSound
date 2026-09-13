using System.Net;
using RemSound.Core;
using RemSound.Receiver;

namespace RemSound.App;

internal static partial class SelfTest
{
    /// <summary>
    /// "APPLY PAN AND EQ TO PLUGIN AUDIO" MUST ACTUALLY CHANGE THE AUDIO.
    ///
    /// <para>Until 2026-09-06 that menu item did nothing at all. It saved its tick, reloaded it on
    /// restart and wrote a line to the log — and no code anywhere read the value back, so a DAW track
    /// always received shaped audio however the item was set.</para>
    ///
    /// <para>There WAS a test for it. It checked that the setting survived a save and reload, which is
    /// exactly the trap the dead latency slider taught us about: proving a value is STORED proves
    /// nothing about whether anything acts on it. Menu items are not among the 76 controls the control
    /// suite drives, so nothing else caught it either.</para>
    ///
    /// <para>So this one MEASURES. It pans a peer hard left, reads the same peer through the plugin's
    /// own path with the setting on and then off, and requires the two to differ in the direction the
    /// pan dictates. Nothing here asks what the flag is set to — the audio answers.</para>
    ///
    /// <para>Not per-configuration: a claimed peer belongs to a DAW track, which has no output lane at
    /// all. <c>ReadClaimedPeer</c> deliberately does not filter by lane, and shaping happens inside the
    /// peer's own session read, before any lane is involved. The three configurations decide which
    /// SPEAKER lanes exist, and a claimed peer is excluded from every one of them.</para>
    /// </summary>
    private static string? AuditPluginShapingSwitchActuallySwitches()
    {
        var peer = new IPEndPoint(IPAddress.Parse("192.168.1.66"), 47830);

        static float PeakOfChannel(float[] block, int channel)
        {
            var peak = 0f;
            for (var i = channel; i < block.Length; i += 2) peak = Math.Max(peak, Math.Abs(block[i]));
            return peak;
        }

        // Read one block of a claimed peer with shaping on or off, from a freshly-built engine so the
        // two runs cannot influence each other through ring state.
        static float[] ReadClaimed(bool applyShaping, IPEndPoint who)
        {
            var engine = new PlayoutEngine(new ReceiverDiagnostics());
            engine.SetLaneActive(RenderRoute.WasapiLane, true);
            engine.SetLaneActive(RenderRoute.AsioLane, false);
            engine.SetMaxLatencyMs(RenderRoute.Mixed, 30);
            engine.SetPluginShaping(applyShaping);

            var session = engine.GetOrCreateSession(who, 1, 1024 * 1024);
            FillSession(session, 0.5f);   // equal in both channels before any shaping

            // Hard left. A pan is the cleanest thing to measure: it moves one channel and not the
            // other, so a single peak comparison answers "did the shaping run".
            engine.SetPeerDsp(who.Address, PeerDspChain.Build(new PeerShaping { Pan = -1f }, enabled: true));

            var claims = new PluginPeerClaims();
            claims.Claim(who.Address, Guid.NewGuid());
            engine.SetPluginPeerClaims(claims);

            var block = new float[960 * 2];
            engine.ReadClaimedPeer(who.Address, block, 960);
            return block;
        }

        var shaped = ReadClaimed(applyShaping: true, peer);
        var raw = ReadClaimed(applyShaping: false, peer);

        var shapedLeft = PeakOfChannel(shaped, 0);
        var shapedRight = PeakOfChannel(shaped, 1);
        var rawLeft = PeakOfChannel(raw, 0);
        var rawRight = PeakOfChannel(raw, 1);

        // The premise: both runs must actually carry audio, or every comparison below is vacuous.
        Check(shapedLeft > 0.05f && rawLeft > 0.05f,
            $"both runs must deliver real audio to the plugin path or this proves nothing "
            + $"(shaped L {shapedLeft:0.000}, raw L {rawLeft:0.000})");

        // ON: panned hard left, so the right channel must be all but gone.
        Check(shapedRight < shapedLeft * 0.25f,
            $"with \"apply pan and EQ to plugin audio\" ON, a peer panned hard left must reach the DAW panned — the "
            + $"right channel should be far below the left (L {shapedLeft:0.000}, R {shapedRight:0.000})");

        // OFF: the pan must NOT have been applied, so the two channels stay as they arrived.
        Check(rawRight > rawLeft * 0.75f,
            $"with the setting OFF the DAW must get the RAW peer — the hard-left pan must NOT have been applied, so both "
            + $"channels should still be level (L {rawLeft:0.000}, R {rawRight:0.000}). This is the check the old test "
            + "never made: it only asked whether the setting survived a save, which it always did while doing nothing");

        // And the two must genuinely differ. Stated separately so a future change that quietly makes
        // both paths identical fails here rather than passing two one-sided checks.
        Check(Math.Abs(shapedRight - rawRight) > 0.05f,
            $"the setting must change the audio. Shaped right {shapedRight:0.000} against raw right {rawRight:0.000} — "
            + "if these match, the switch is decorative again");

        return $"the plugin shaping switch is measured, not assumed: ON pans the DAW's copy (R {shapedRight:0.00} vs L "
             + $"{shapedLeft:0.00}), OFF leaves it raw (R {rawRight:0.00} vs L {rawLeft:0.00})";
    }
}
