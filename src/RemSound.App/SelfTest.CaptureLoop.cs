using RemSound.Sender;

namespace RemSound.App;

internal static partial class SelfTest
{
    /// <summary>
    /// A CARD WHOSE LOOPBACK EVENT NEVER FIRES MUST STILL SEND ALL OF ITS AUDIO.
    ///
    /// <para>Reported from the field, September 2026: audio arriving at the far end broken up several
    /// times a second, unusable. Only about a third of the sound was ever leaving the sending machine.
    /// Both ends' logs agreed — nothing was lost in transit, because only a third was ever sent.</para>
    ///
    /// <para><b>The mechanism, and the arithmetic is exact.</b> RemSound asked WASAPI to signal an
    /// event whenever new audio was ready, and used NAudio's loop, which waits THREE buffer-lengths
    /// for that event and then takes ONE buffer. On a card that signals, the wait returns at the device
    /// period every time and nothing is missed. On a card that never signals — an ordinary built-in
    /// one, in this case — the wait times out after three buffers, one buffer is taken, and WASAPI has
    /// long since overwritten the other two. One in three. 33 %.</para>
    ///
    /// <para><b>Why this test is being written before the fix rather than after.</b> The loop lived
    /// inside NAudio, so RemSound owned no code that could be tested and this was invisible from the
    /// sending end for years — the client initialises fine, nothing errors, the card simply goes quiet
    /// on the event. The pull request that reports it contains no test. Taking the fix without one
    /// would repair the fault and leave the hole that hid it.</para>
    ///
    /// <para><b>Not per-configuration.</b> This is WASAPI loopback capture. ASIO capture runs on its
    /// own driver callback with no WASAPI event anywhere in it, so there is nothing to compare and
    /// nothing that could differ — the ASIO lane cannot reach this code at all. Which of the three
    /// configurations is live is decided by OUTPUT ticks and has no bearing on how a capture device is
    /// polled.</para>
    /// </summary>
    private static string? AuditCaptureSurvivesADeadDeviceEvent()
    {
        const int bufferMs = 10;
        const int framesPerPacket = 480;      // 10 ms at 48 kHz

        // THE CARD THAT NEVER SIGNALS. Every wake is the timeout expiring; the event is never set.
        var silentCard = new SimulatedCaptureDevice(bufferMs, framesPerPacket);
        var captured = RunLoop(silentCard, eventFires: false, seconds: 12);
        var expected = 12 * 1000 / bufferMs * framesPerPacket;
        var share = captured / (double)expected;

        Check(share > 0.99,
            $"a card whose loopback event never fires must still deliver ALL of its audio — got {share:P0} "
            + $"({captured} frames of {expected}). At a third of the audio the far end hears a stutter several times a "
            + "second, which is exactly what was reported: the two thirds are not delayed, they are overwritten by "
            + "WASAPI before anyone looks");

        // THE CARD THAT DOES SIGNAL. This row matters as much as the first: whatever we do for the
        // broken card must not disturb hardware that was always fine — which is every machine we
        // develop on, and why this went unnoticed for so long.
        var goodCard = new SimulatedCaptureDevice(bufferMs, framesPerPacket);
        var goodCaptured = RunLoop(goodCard, eventFires: true, seconds: 12);
        var goodShare = goodCaptured / (double)expected;
        Check(goodShare > 0.99,
            $"a card that DOES signal must be unaffected — got {goodShare:P0} ({goodCaptured} frames of {expected})");

        // NOTHING MAY BE OVERWRITTEN, on either card. This is the strongest form of the claim and the
        // one the field report is about: not "we read often enough on average" but "no audio was ever
        // produced and then destroyed before we looked at it".
        Check(silentCard.FramesLost == 0,
            $"on a card whose event never fires, no audio may be overwritten before it is read "
            + $"({silentCard.FramesLost} frames lost) — every lost frame is a hole the far end fills with silence");
        Check(goodCard.FramesLost == 0,
            $"on a signalling card, no audio may be overwritten either ({goodCard.FramesLost} frames lost)");

        // AND THE COST, STATED RATHER THAN ASSUMED. A half-buffer ceiling against a full-buffer period
        // means roughly half the wakes on a HEALTHY card are the timeout expiring and finding nothing.
        // That is a real doubling of capture-thread wake-ups on hardware that never had the problem,
        // and it is the price of not depending on the event. It is bounded — never a spin — and this
        // pins the bound so it cannot quietly get worse.
        var wakesPerSecond = (goodCard.WokenByEvent + goodCard.WokenByTimeout) / 12.0;
        var periodsPerSecond = 1000.0 / bufferMs;
        Check(wakesPerSecond <= periodsPerSecond * 2.5,
            $"on a healthy card the loop must wake at most about twice per device period "
            + $"({wakesPerSecond:0} wakes/s against {periodsPerSecond:0} periods/s) — more than that is a spin, and a "
            + "spin on the capture thread costs every user something to fix a fault most of them do not have");
        Check(goodCard.WokenByEvent >= goodCard.WokenByTimeout,
            $"a signalling card must still be driven mostly by its own hardware clock ({goodCard.WokenByEvent} event "
            + $"wakes vs {goodCard.WokenByTimeout} timeouts), not paced by our timer");

        // THE POLL CEILING ITSELF. Half a buffer, bounded. A ceiling longer than one buffer cannot
        // guarantee anything: the data is gone by then.
        Check(WasapiCaptureLoop.PollTimeoutMs(bufferMs) < bufferMs,
            $"the wait ceiling must be shorter than one buffer (got {WasapiCaptureLoop.PollTimeoutMs(bufferMs)} ms for a "
            + $"{bufferMs} ms buffer) — waiting a whole buffer or more is how audio gets overwritten before it is read");
        Check(WasapiCaptureLoop.PollTimeoutMs(2) >= WasapiCaptureLoop.MinPollMs,
            "a tiny buffer must not spin the capture thread");
        Check(WasapiCaptureLoop.PollTimeoutMs(1000) <= WasapiCaptureLoop.MaxPollMs,
            "a very large buffer must still be looked at often enough to matter");

        return $"a card whose event never fires delivers {share:P0} of its audio with nothing overwritten; a signalling card is unaffected at {goodShare:P0}; cost on a healthy card is {wakesPerSecond:0} wakes/s against {periodsPerSecond:0} device periods";
    }

    /// <summary>Drive the real loop against a simulated device for a stretch of virtual time.</summary>
    private static int RunLoop(SimulatedCaptureDevice device, bool eventFires, int seconds)
    {
        var captured = 0;
        var deadline = seconds * 1000;
        while (device.NowMs < deadline)
        {
            // The real loop waits for the event with a timeout, then drains. The device decides which
            // of the two happened and advances virtual time accordingly.
            device.Wait(WasapiCaptureLoop.PollTimeoutMs(device.BufferMs), eventFires);
            captured += WasapiCaptureLoop.Drain(device);
        }
        return captured;
    }

    /// <summary>
    /// A WASAPI capture endpoint, in virtual time.
    ///
    /// <para>The one behaviour that matters and that a mock would be tempted to leave out: the device
    /// buffer holds ONE buffer-length of audio, and when the next period arrives with the previous one
    /// still unread, the old audio is <b>overwritten</b> — not queued. That is the whole bug. A
    /// simulation with an unbounded queue would show a loop that waits too long as merely bursty
    /// rather than lossy, and would pass against the broken code.</para>
    /// </summary>
    private sealed class SimulatedCaptureDevice : ICapturePacketReader
    {
        private readonly int framesPerPacket;
        private int queuedPackets;
        private double producedUpToMs;

        public SimulatedCaptureDevice(int bufferMs, int framesPerPacket)
        {
            BufferMs = bufferMs;
            this.framesPerPacket = framesPerPacket;
        }

        public int BufferMs { get; }
        public double NowMs { get; private set; }
        public int WokenByEvent { get; private set; }
        public int WokenByTimeout { get; private set; }
        /// <summary>Frames the device produced and then overwrote because nobody read them in time.</summary>
        public int FramesLost { get; private set; }

        /// <summary>Wait for the device event, up to <paramref name="timeoutMs"/>. A signalling card
        /// wakes us the moment a period completes; a silent one leaves us to time out.</summary>
        public void Wait(int timeoutMs, bool eventFires)
        {
            var nextPeriodAtMs = producedUpToMs + BufferMs;
            if (eventFires && nextPeriodAtMs - NowMs <= timeoutMs)
            {
                AdvanceTo(nextPeriodAtMs);
                WokenByEvent++;
            }
            else
            {
                AdvanceTo(NowMs + timeoutMs);
                WokenByTimeout++;
            }
        }

        private void AdvanceTo(double whenMs)
        {
            NowMs = whenMs;
            // Produce every period that has elapsed. The buffer holds one; anything arriving on top of
            // an unread one destroys it, exactly as WASAPI does.
            while (producedUpToMs + BufferMs <= NowMs + 1e-9)
            {
                producedUpToMs += BufferMs;
                if (queuedPackets >= 1) FramesLost += framesPerPacket;
                else queuedPackets = 1;
            }
        }

        public int NextPacketFrames => queuedPackets > 0 ? framesPerPacket : 0;

        public int ConsumePacket()
        {
            if (queuedPackets <= 0) return 0;
            queuedPackets--;
            return framesPerPacket;
        }
    }
}
