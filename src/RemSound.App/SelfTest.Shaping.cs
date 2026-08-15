using RemSound.Core;
using RemSound.Receiver;

namespace RemSound.App;

/// <summary>
/// VOLUME, PAN AND EQ — measured, not assumed.
///
/// <para>Ed, 2026-08-15: "have you made complete coverage tests to test every single control in the EQ
/// and pan section including all 3 modes of the EQ, that it does actually change the sound profile?"
/// The honest answer at the time was no. The control suite audited these controls for accessibility
/// and theme, and every one of them was declared as governing nothing — which was simply false. The
/// DSP test proved a chain got BUILT, which is not the same claim as the audio changing.</para>
///
/// <para><b>So this measures the audio.</b> A tone goes in, the shaped result comes out, and the test
/// asserts on real levels: energy at the boosted frequency rises, energy well away from it does not,
/// pan moves it between the channels. A filter with the wrong sign, the wrong centre frequency or the
/// wrong Q would build a perfectly healthy chain and sound wrong, and only a measurement catches
/// that.</para>
///
/// <para><b>All three modes, because they are three separate code paths.</b> 3-band tone control,
/// 12-band graphic, 16-band parametric. Proving one works says nothing about the other two — and the
/// mode chooser itself is what selects between them, so it is checked that switching mode actually
/// changes the result.</para>
/// </summary>
internal static partial class SelfTest
{
    private const int ShapingRate = PeerEqBands.MixSampleRate;

    private static string? ShapingVolumeAndPan()
    {
        var findings = new List<string>();

        // --- Volume ------------------------------------------------------------------------------
        var quiet = Rms(Shape(new PeerShaping { Volume = 0.5f }, 1000));
        var unity = Rms(Tone(1000));
        Check(Math.Abs(quiet / unity - 0.5) < 0.02, $"volume at 50% must halve the level (measured {quiet / unity:0.000} of unity)");
        var silent = Rms(Shape(new PeerShaping { Volume = 0f }, 1000));
        Check(silent < 0.001, $"volume at zero must be silent (measured {silent:0.0000})");
        findings.Add($"volume 50% measured {quiet / unity:0.00}x");

        // --- Pan. THE test the suite never had: does the sound actually move? --------------------
        var centre = Shape(new PeerShaping(), 1000);
        var (centreL, centreR) = ChannelRms(centre);
        Check(Math.Abs(centreL - centreR) < 0.001, $"centred, both channels must be equal ({centreL:0.000} vs {centreR:0.000})");

        var (hardLeftL, hardLeftR) = ChannelRms(Shape(new PeerShaping { Pan = -1f }, 1000));
        Check(hardLeftR < hardLeftL * 0.05,
            $"panned hard left, the RIGHT channel must fall away ({hardLeftR:0.000} against left {hardLeftL:0.000})");
        Check(hardLeftL > centreL * 0.9, $"...while the left channel keeps its level ({hardLeftL:0.000} vs centred {centreL:0.000})");

        var (hardRightL, hardRightR) = ChannelRms(Shape(new PeerShaping { Pan = 1f }, 1000));
        Check(hardRightL < hardRightR * 0.05,
            $"panned hard right, the LEFT channel must fall away ({hardRightL:0.000} against right {hardRightR:0.000})");

        // Half-left must land BETWEEN centred and hard left, or the law is stepped rather than smooth
        // and every intermediate position is wrong even though both extremes look right.
        var (halfL, halfR) = ChannelRms(Shape(new PeerShaping { Pan = -0.5f }, 1000));
        Check(halfR < centreR && halfR > hardLeftR,
            $"half left must sit between centred and hard left, not snap to an extreme (right channel {halfR:0.000}; centre {centreR:0.000}, hard {hardLeftR:0.000})");
        findings.Add($"pan hard left leaves {hardLeftR / centreR:0.000} of the right channel");

        // --- The two bypasses -----------------------------------------------------------------------
        Check(PeerDspChain.Build(new PeerShaping { Volume = 0.5f }, enabled: false) is null,
            "the master switch off must bypass shaping entirely");

        // The per-peer tick is combined with the master switch IN THE WINDOW, not inside Build. So it
        // is driven through the window's own decision — calling Build with a peer's tick off would be
        // testing a call the app never makes, which is how a bypass ends up looking covered and isn't.
        MainForm? form = null;
        try
        {
            var profile = Profile.NewBlank();
            profile.Password = RemSoundCrypto.Obfuscate("shaping-test-password");
            try { form = new MainForm(null, profile, null, null, headless: true); }
            catch (Exception ex) { return Skip($"headless MainForm could not be constructed: {ex.GetType().Name}: {ex.Message}"); }

            const string Peer = "192.168.1.50";
            Check(form.ShapingActiveForTest(Peer, masterOn: true, peerTicked: true), "master on and the peer ticked must shape them");
            Check(!form.ShapingActiveForTest(Peer, masterOn: false, peerTicked: true), "the master switch off must win over a ticked peer");
            Check(!form.ShapingActiveForTest(Peer, masterOn: true, peerTicked: false),
                "a peer with their own tick OFF must be left alone even with the master switch on - that is the per-peer bypass");
            Check(!form.ShapingActiveForTest(Peer, masterOn: false, peerTicked: false), "both off is off");
        }
        finally { try { form?.Dispose(); } catch { } }

        return string.Join("; ", findings) + "; zero is silent; centred is equal; both bypasses hold (master, and the per-peer tick)";
    }

    /// <summary>Each of the three EQ modes, measured at a frequency it should move and one it should
    /// not. A filter with the wrong sign or the wrong centre builds a perfectly healthy chain.</summary>
    private static string? ShapingEqModes()
    {
        var findings = new List<string>();

        // --- 1. Simple 3-band: bass / mids / treble ------------------------------------------------
        var bass = new PeerShaping { EqMode = PeerEqMode.Simple3Band };
        bass.SimpleBandsDb[0] = 12f;                       // bass, a low shelf at 100 Hz
        Check(GainDb(bass, 60) > 6, $"3-band: boosting bass must lift 60 Hz (measured {GainDb(bass, 60):0.0} dB)");
        Check(Math.Abs(GainDb(bass, 8000)) < 1.5, $"...and leave 8 kHz alone (measured {GainDb(bass, 8000):0.0} dB)");

        var treble = new PeerShaping { EqMode = PeerEqMode.Simple3Band };
        treble.SimpleBandsDb[2] = 12f;                     // treble, a high shelf at 8 kHz
        Check(GainDb(treble, 12000) > 6, $"3-band: boosting treble must lift 12 kHz (measured {GainDb(treble, 12000):0.0} dB)");
        Check(Math.Abs(GainDb(treble, 100)) < 1.5, $"...and leave 100 Hz alone (measured {GainDb(treble, 100):0.0} dB)");

        // A CUT must cut. A sign error passes every boost test ever written.
        var cutMids = new PeerShaping { EqMode = PeerEqMode.Simple3Band };
        cutMids.SimpleBandsDb[1] = -12f;
        Check(GainDb(cutMids, 1000) < -4, $"3-band: cutting mids must REDUCE 1 kHz (measured {GainDb(cutMids, 1000):0.0} dB)");
        findings.Add($"3-band: bass +{GainDb(bass, 60):0.0} dB at 60 Hz, treble +{GainDb(treble, 12000):0.0} dB at 12 kHz, mids {GainDb(cutMids, 1000):0.0} dB at 1 kHz");

        // --- 2. Twelve-band graphic --------------------------------------------------------------
        // Every band is checked, at its own centre frequency. One band wired to the wrong slider is
        // invisible unless each is driven separately, and it is exactly the mistake a 12-element
        // array invites.
        var worstBand = "";
        var worstGain = 99.0;
        for (var band = 0; band < PeerEqBands.Advanced.Length; band++)
        {
            var shaping = new PeerShaping { EqMode = PeerEqMode.Advanced10Band };
            shaping.AdvancedBandsDb[band] = 12f;
            var centreHz = PeerEqBands.Advanced[band].Freq;
            var gain = GainDb(shaping, centreHz);
            if (gain < worstGain) { worstGain = gain; worstBand = PeerEqBands.Advanced[band].Label; }
            Check(gain > 5,
                $"12-band: band {band} ({PeerEqBands.Advanced[band].Label}) must lift its own centre frequency (measured {gain:0.0} dB)");

            // ...and must not lift a frequency two decades away. A Q so wide it moves everything is
            // a graphic EQ in name only.
            var farHz = centreHz < 1000 ? 12000.0 : 60.0;
            Check(Math.Abs(GainDb(shaping, farHz)) < 3,
                $"12-band: band {PeerEqBands.Advanced[band].Label} must leave {farHz:0} Hz roughly alone (measured {GainDb(shaping, farHz):0.0} dB)");
        }
        findings.Add($"12-band: all {PeerEqBands.Advanced.Length} bands lift their own centre (weakest {worstBand} at +{worstGain:0.0} dB)");

        // --- 3. Sixteen-band parametric ------------------------------------------------------------
        var para = new PeerShaping { EqMode = PeerEqMode.Parametric16Band };
        para.ParametricBands.Add(new ParametricBand { StartHz = 800, EndHz = 1250, GainDb = 12 });
        PeerEqBands.ParametricToPeaking(800, 1250, out var paraCentre, out _);
        Check(GainDb(para, paraCentre) > 6, $"parametric: a boost must lift the middle of its range (measured {GainDb(para, paraCentre):0.0} dB at {paraCentre:0} Hz)");
        Check(Math.Abs(GainDb(para, 100)) < 2, $"...and leave 100 Hz alone (measured {GainDb(para, 100):0.0} dB)");
        Check(Math.Abs(GainDb(para, 12000)) < 2, $"...and 12 kHz (measured {GainDb(para, 12000):0.0} dB)");

        var paraCut = new PeerShaping { EqMode = PeerEqMode.Parametric16Band };
        paraCut.ParametricBands.Add(new ParametricBand { StartHz = 800, EndHz = 1250, GainDb = -12 });
        Check(GainDb(paraCut, paraCentre) < -4, $"parametric: a cut must REDUCE its range (measured {GainDb(paraCut, paraCentre):0.0} dB)");

        // Two bands must both apply, not just the last one added.
        var twoBands = new PeerShaping { EqMode = PeerEqMode.Parametric16Band };
        twoBands.ParametricBands.Add(new ParametricBand { StartHz = 80, EndHz = 125, GainDb = 12 });
        twoBands.ParametricBands.Add(new ParametricBand { StartHz = 6000, EndHz = 9000, GainDb = 12 });
        Check(GainDb(twoBands, 100) > 5 && GainDb(twoBands, 7500) > 5,
            $"parametric: two bands must BOTH apply (low {GainDb(twoBands, 100):0.0} dB, high {GainDb(twoBands, 7500):0.0} dB)");
        findings.Add($"parametric: +{GainDb(para, paraCentre):0.0} dB in range, {GainDb(paraCut, paraCentre):0.0} dB cut, two bands both apply");

        // --- The mode chooser itself governs something ---------------------------------------------
        // Same slider values, different mode, different result. This is the control the suite declared
        // as governing nothing.
        var asSimple = new PeerShaping { EqMode = PeerEqMode.Simple3Band };
        asSimple.SimpleBandsDb[0] = 12f;
        var asGraphic = new PeerShaping { EqMode = PeerEqMode.Advanced10Band };
        asGraphic.AdvancedBandsDb[0] = 12f;                // slider 0 here is a 31 Hz peaking band
        Check(Math.Abs(GainDb(asSimple, 8000) - GainDb(asGraphic, 8000)) < 3, "both modes should leave 8 kHz alone");
        // Measured at 100 Hz: that is the 3-band shelf's own corner, and two octaves above the
        // graphic mode's first band. Anywhere higher and the shelf has already rolled off, which is
        // correct behaviour and would make this look like a weak difference rather than a real one.
        Check(GainDb(asSimple, 100) > GainDb(asGraphic, 100) + 4,
            $"the EQ MODE must change the result: slider 0 is a 100 Hz bass shelf in 3-band mode and a 31 Hz band in "
          + $"graphic mode, so 100 Hz must move very differently ({GainDb(asSimple, 100):0.0} dB vs {GainDb(asGraphic, 100):0.0} dB)");

        // Unity in every mode must stay a no-op, or a peer with a mode chosen and flat sliders would
        // pay for filters that do nothing.
        foreach (var mode in new[] { PeerEqMode.Simple3Band, PeerEqMode.Advanced10Band, PeerEqMode.Parametric16Band })
            Check(PeerDspChain.Build(new PeerShaping { EqMode = mode }, enabled: true) is null,
                $"flat sliders in {mode} must build no chain at all");

        // Gain is clamped, so a wild value cannot produce a screaming boost.
        var overdriven = new PeerShaping { EqMode = PeerEqMode.Simple3Band };
        overdriven.SimpleBandsDb[1] = 100f;
        Check(GainDb(overdriven, 1000) < PeerEqBands.MaxGainDb + 3,
            $"a band gain beyond the slider's range must be clamped, not applied (measured {GainDb(overdriven, 1000):0.0} dB)");

        return string.Join("; ", findings) + "; the mode chooser changes the result; flat is a no-op in all three; gain is clamped";
    }

    // ---- Measurement ---------------------------------------------------------------------------

    /// <summary>One second of a stereo sine at the given frequency, at half scale so a 12 dB boost has
    /// somewhere to go without hitting the limiter and flattening the very thing being measured.</summary>
    private static float[] Tone(double hz, int seconds = 1)
    {
        var frames = ShapingRate * seconds;
        var buffer = new float[frames * 2];
        var step = 2 * Math.PI * hz / ShapingRate;
        for (var i = 0; i < frames; i++)
        {
            var v = (float)(Math.Sin(i * step) * 0.25);
            buffer[i * 2] = v;
            buffer[i * 2 + 1] = v;
        }
        return buffer;
    }

    /// <summary>Push a tone through the real shaping chain and hand back what came out.</summary>
    private static float[] Shape(PeerShaping shaping, double hz)
    {
        var buffer = Tone(hz);
        var chain = PeerDspChain.Build(shaping, enabled: true);
        // A null chain is the honest answer for unity settings — the caller measures what it gets.
        chain?.Process(buffer, buffer.Length / 2);
        return buffer;
    }

    /// <summary>How much the shaping changed the level at one frequency, in dB. The whole point: a
    /// number to assert on rather than "a chain was built".</summary>
    private static double GainDb(PeerShaping shaping, double hz)
    {
        var plain = Rms(Tone(hz));
        var shaped = Rms(Shape(shaping, hz));
        if (plain <= 0) return 0;
        return 20 * Math.Log10(Math.Max(shaped, 1e-9) / plain);
    }

    /// <summary>RMS of the whole buffer, skipping the first 100 ms so a filter's settling transient
    /// never lands in the number.</summary>
    private static double Rms(float[] interleaved)
    {
        var skip = ShapingRate / 10 * 2;
        if (interleaved.Length <= skip) return 0;
        var sum = 0.0;
        for (var i = skip; i < interleaved.Length; i++) sum += (double)interleaved[i] * interleaved[i];
        return Math.Sqrt(sum / (interleaved.Length - skip));
    }

    private static (double Left, double Right) ChannelRms(float[] interleaved)
    {
        var skip = ShapingRate / 10 * 2;
        double left = 0, right = 0;
        var count = 0;
        for (var i = skip; i + 1 < interleaved.Length; i += 2)
        {
            left += (double)interleaved[i] * interleaved[i];
            right += (double)interleaved[i + 1] * interleaved[i + 1];
            count++;
        }
        if (count == 0) return (0, 0);
        return (Math.Sqrt(left / count), Math.Sqrt(right / count));
    }
}
