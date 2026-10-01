using System.Net;
using System.Runtime.InteropServices;
using Concentus;
using RemSound.Core;

namespace RemSound.Receiver;

/// <summary>
/// Owns the decode pipeline for one incoming stream, keyed by (sender endpoint, stream id) — a
/// sender with several lanes has one per lane. When a stream's format changes, the receiver swaps
/// in a new StreamSession over the same SessionPlayout, so buffered audio drains out naturally
/// rather than being thrown away mid-playback; a stream id the sender has rotated away from on
/// the same lane is superseded and closed.
///
/// Packets arrive on two threads - the network listener's, and the sender socket's for audio from a server - and the
/// session can be closed from a third. A packet being handled and a close take their turn under one small lock (see
/// HandleAudioPayload); decoded frames go to the SPSC <see cref="AudioRingBuffer"/>.
/// </summary>
internal sealed class StreamSession : IDisposable
{
    private readonly SessionPlayout sessionPlayout;
    private readonly ReceiverDiagnostics diagnostics;
    private readonly Action<int> onFramesQueued;
    private readonly AudioDecryptor decryptor;
    private readonly PcmFrameAssembler pcmAssembler = new();
    private IOpusDecoder? opusDecoder;
    // Sequence-tracking for Opus FEC recovery. uint, so wrap-around is naturally
    // handled by the (current - expected == 1U) comparison at gap detection.
    private uint? expectedNextSequence;
    /// <summary>Number of single-packet gaps recovered using inband FEC from the next packet.</summary>
    public long OpusFecRecoveries { get; private set; }
    /// <summary>Number of multi-packet gaps where FEC could not help (only logs once per occurrence).</summary>
    public long OpusUnrecoveredGaps { get; private set; }

    public IPEndPoint Endpoint { get; }
    public ushort StreamId { get; }
    public AudioFormatInfo Format { get; }
    public AudioTransportCodec Codec => (AudioTransportCodec)Format.Codec;

    /// <summary>UTC timestamp of the most recent decoded-audio write into this session's
    /// playout buffer. <see cref="AudioReceiver.PruneIdleSessions"/> reaps on this directly,
    /// rather than a cross-dictionary lookup into PlayoutEngine that could miss and strand
    /// the session forever — a reconnecting peer never reuses its old (endpoint, streamId)
    /// key, so its previous session is always an orphan that must be reaped by idle age.</summary>
    public DateTime LastWriteUtc => sessionPlayout.LastWriteUtc;

    /// <summary>This stream's own packet-arrival gaps (the latency lab's per-stream buffers).</summary>
    internal StreamArrivalStats Arrivals { get; } = new();

    /// <summary>The primary replica this stream decodes into (mirrors hang off it).</summary>
    internal SessionPlayout Playout => sessionPlayout;
    /// <summary>Times this stream's playback waited to refill after a real stop (SessionPlayout.LongGapRearmMs).</summary>
    internal long LongGapRearmsForTest => sessionPlayout.LongGapRearmsForTest;

    private uint? highestSequence;
    private int lateInARow;

    /// <summary>A jump back further than this many packets is a sender that started counting again, not a late packet.</summary>
    internal const uint SequenceRestartJump = 1000;

    /// <summary>This many "late" packets in a row is a sender that started counting again - an overtaken packet is always
    /// followed by newer ones.</summary>
    internal const int SequenceRestartRun = 8;

    /// <summary>Doubled or overtaken packets dropped because they were no newer than the newest already handled.</summary>
    public long LateDropped { get; private set; }

    /// <summary>Times the sender was seen to start counting again on this stream (followed, not dropped).</summary>
    public long SequenceRestarts { get; private set; }

    /// <summary>For the lab line: late or doubled packets dropped, restarts followed, and concealed frames on a
    /// Jamulus-style stream.</summary>
    internal string LabExtra => (LateDropped > 0 ? $" late-dropped={LateDropped}" : "") + (SequenceRestarts > 0 ? $" restarts={SequenceRestarts}" : "")
        + (ReorderWaits > 0 ? $" reorder-waits={ReorderWaits} filled={ReorderFilled}" : "")
        + (Codec == AudioTransportCodec.OpusCustom ? $" concealed={CustomConcealedFrames}" : "");
    internal long AudioBytesWrittenForTest => sessionPlayout.AudioBytesWrittenForTest;

    /// <summary>For PCM streams: number of incoming packets the assembler rejected outright.</summary>
    public long PcmFrameRejections => pcmAssembler.RejectionCount;
    /// <summary>For PCM streams: number of partially-assembled frames discarded mid-flight.</summary>
    public long PcmFrameDiscardedPartials => pcmAssembler.DiscardedPartialCount;

    // Post-decode discontinuity probe. Scans the float buffer right after Int24LEToFloat
    // (PCM) or short-to-float (Opus) so we can compare to the sender's pre-encode probe and
    // detect any wire-level or decode-level artefacts. Same buffer is then handed to the
    // session playout, so the post-ring-read probe in SessionPlayout sees the exact same
    // samples a moment later (after riding through the ring buffer).
    private readonly AudioStepProbe postDecodeStepProbe = new();
    public float TakeMaxPostDecodeStepCrossBuffer() => postDecodeStepProbe.TakeMaxCrossBuffer();
    public float TakeMaxPostDecodeStepWithinBuffer() => postDecodeStepProbe.TakeMaxWithinBuffer();

    // === Wire-level sequence tracking (Phase 5, 2026-05-14) ===
    // Every audio packet carries a wire sequence number that monotonically increases per
    // session (audioSequence in SenderLane). The Opus path uses this for FEC recovery. The
    // PCM path historically ignored it entirely. Now we track it to detect:
    //   * MISSING packets — sequence > expected (gap > 1 frames)
    //   * REORDERED packets — sequence < expected (a packet arrived after a later one)
    //   * DUPLICATE packets — sequence == previous (same packet delivered twice)
    //   * IN-ORDER packets — sequence == expected
    //
    // Any of MISSING / REORDERED / DUPLICATE on a healthy LAN would point straight at a
    // transport-level issue (NIC offload bug, switch buffer overflow, RSS hash collision
    // causing packets to take different queues). MISSING on PCM = silent audio drop at
    // the packet boundary = audible click. REORDERED = the receiver processes audio in
    // the wrong order = audible click. DUPLICATE = same audio played twice in a row =
    // audible click.
    private uint? expectedNextWireSequence;
    private long wireInOrderTotal;
    private long wireMissedTotal;     // sum of missing-packet counts (sequence > expected by N → +N)
    private long wireReorderedTotal;  // count of times a sequence < expected arrived
    private long wireDuplicatedTotal; // count of times a sequence == previous arrived
    public long WireInOrderCount => Interlocked.Read(ref wireInOrderTotal);
    public long WireMissedCount => Interlocked.Read(ref wireMissedTotal);
    public long WireReorderedCount => Interlocked.Read(ref wireReorderedTotal);
    public long WireDuplicatedCount => Interlocked.Read(ref wireDuplicatedTotal);

    public StreamSession(
        IPEndPoint endpoint,
        ushort streamId,
        AudioFormatInfo format,
        SessionPlayout sessionPlayout,
        ReceiverDiagnostics diagnostics,
        Action<int> onFramesQueued,
        AudioDecryptor decryptor)
    {
        Endpoint = endpoint;
        StreamId = streamId;
        Format = format;
        this.sessionPlayout = sessionPlayout;
        this.diagnostics = diagnostics;
        this.onFramesQueued = onFramesQueued;
        this.decryptor = decryptor;

        if (Codec == AudioTransportCodec.Opus)
        {
            opusDecoder = OpusCodecFactory.CreateDecoder(format.SampleRate, format.Channels, TextWriter.Null);
        }
        else if (Codec == AudioTransportCodec.OpusCustom)
        {
            // AudioFormatInfo.IsUsable has already checked the frame size and that remsound-opus.dll loads.
            customDecoder = new OpusCustomDecoder(format.FrameSamplesPerChannel, format.Channels);
        }
    }

    // Jamulus-style Opus: its own decoder, and packet-loss concealment for missing frames.
    private OpusCustomDecoder? customDecoder;
    private uint? customExpectedSequence;
    /// <summary>Frames made up by Opus concealment because their packets never came (Jamulus-style streams only).</summary>
    public long CustomConcealedFrames { get; private set; }

    /// <summary>
    /// One Jamulus-style packet. A gap of up to a few packets is filled with Opus concealment first, one made-up frame per
    /// missing packet, so the audio stays continuous and in time the way Jamulus does it; a longer gap is a stream that
    /// stopped, and the buffer's own handling applies.
    /// </summary>
    private bool HandleOpusCustom(uint sequence, ReadOnlySpan<byte> payload)
    {
        var expected = customExpectedSequence;
        customExpectedSequence = sequence + 1U;
        var decoder = customDecoder;
        if (decoder is null) return false;
        payload = decryptor.TryDecrypt(payload);
        if (payload.IsEmpty) return false;
        var frame = decoder.FrameSamplesPerChannel;
        var floats = frame * decoder.Channels;
        Span<float> pcm = floats <= 1024 ? stackalloc float[floats] : new float[floats];
        if (expected is uint want)
        {
            var missing = sequence - want;
            if (missing is >= 1 and <= 8)
            {
                for (var i = 0u; i < missing; i++)
                {
                    var made = decoder.Conceal(pcm);
                    if (made <= 0) break;
                    WriteDecodedFloats(pcm[..(made * decoder.Channels)], made);
                    CustomConcealedFrames++;
                }
            }
        }
        var got = decoder.Decode(payload, pcm);
        if (got <= 0) return false;
        WriteDecodedFloats(pcm[..(got * decoder.Channels)], got);
        return true;
    }

    private void WriteDecodedFloats(ReadOnlySpan<float> interleaved, int samplesPerChannel)
    {
        postDecodeStepProbe.ScanStereo(interleaved);
        sessionPlayout.Write(MemoryMarshal.AsBytes(interleaved));
        onFramesQueued(samplesPerChannel);
    }

    /// <summary>Returns true if this session matches the given format identity (codec/rate/channels/frame).</summary>
    public bool MatchesFormat(IPEndPoint endpoint, ushort streamId, AudioFormatInfo format) =>
        Endpoint.Equals(endpoint)
        && StreamId == streamId
        && Format.Codec == format.Codec
        && Format.SampleRate == format.SampleRate
        && Format.Channels == format.Channels
        && Format.FrameSamplesPerChannel == format.FrameSamplesPerChannel;

    /// <summary>A packet being handled and <see cref="Dispose"/> take turns. The network thread handles packets outside
    /// the receiver's lock while another thread can close the session - someone unticked, receiving switched off, a
    /// stream replaced - and closing freed the Opus decoder, which holds native memory, possibly in the middle of a decode
    /// (found 2026-09-25).</summary>
    private readonly object handlingGate = new();
    private bool disposed;

    /// <summary>Gate seam: runs while a packet is being handled, inside the turn it holds.</summary>
    internal Action? WhileHandlingForTest { get; set; }

    public bool HandleAudioPayload(uint sequence, ReadOnlySpan<byte> payload)
    {
        lock (handlingGate)
        {
            if (disposed) return false;
            WhileHandlingForTest?.Invoke();
            return HandleAudioPayloadLocked(sequence, payload);
        }
    }

    private bool HandleAudioPayloadLocked(uint sequence, ReadOnlySpan<byte> payload)
    {
        // Already had it on the peer's other path. Handled, not failed: counting it as a drop would
        // make a healthy merged session look like a lossy one. See AlreadyDelivered.
        if (AlreadyDelivered(sequence)) return true;
        diagnostics.RecordPacketArrived();
        Arrivals.Record();
        TrackWireSequence(sequence);
        // A packet no newer than the newest already handled is a doubled copy, or was overtaken on the way, and its
        // moment has passed: the gap it left was repaired or concealed when the newer one arrived. Playing it anyway was
        // heard as a click and a jump in the delay (found by the simulator, 2026-09-26; up to 22 ms of creep in a run).
        // So it is dropped - handled, not failed, so not counted as a drop. Signed difference, so wrap-around is fine.
        //
        // The one thing that looks the same and is not: a sender that starts counting again on the same stream. The Pi
        // sender does exactly that when its program restarts (stream 1, sequence from 0). A jump back of more than
        // SequenceRestartJump packets, or SequenceRestartRun "late" packets in a row (an overtaken packet is followed by
        // newer ones; a restarted sender never is), means it started again: follow it and play on.
        // (highestSequence is the newest packet PLAYED; a packet held by the reorder wait below is not played yet.)
        if (held is { } copy && sequence == copy.Sequence) { LateDropped++; return true; } // a copy of the one being held
        if (highestSequence is uint newest && (int)(sequence - newest) <= 0)
        {
            lateInARow++;
            if (newest - sequence > SequenceRestartJump || lateInARow >= SequenceRestartRun)
            {
                SequenceRestarts++;
                held = null; // it belonged to the old count
                return Play(sequence, payload);
            }
            LateDropped++;
            return true;
        }

        // THE REORDER WAIT (2026-09-26; a latency lab switch until the simulator found it never worse on any network, codec
        // or buffer, and better in 12 places - all of them Ed's laptop pattern). A packet that arrives with a gap before it may just have
        // overtaken the missing one. Hold it until the next packet arrives: if that is the missing one, both play in order
        // and nothing was lost; if it is a newer one, the held packet plays and the gap is repaired exactly as it always
        // was. The wait is only taken while the buffer holds enough sound to cover it (two frames and a millisecond), so a
        // real loss never runs the buffer dry waiting; and never for more than two arrivals.
        if (held is { } waiting)
        {
            if ((int)(sequence - waiting.Sequence) < 0)
            {
                // One of the missing ones: it plays now, in order.
                var ok = Play(sequence, payload);
                if (sequence + 1 == waiting.Sequence) { ReorderFilled++; ReleaseHeld(); }
                else if (++heldArrivals >= 2) ReleaseHeld();
                return ok;
            }
            ReleaseHeld(); // the missing one did not come in time
        }
        var missing = highestSequence is uint last ? (int)(sequence - last) - 1 : 0;
        if (!ReorderWaitOffForTest && missing is >= 1 and <= 3 && CanAffordReorderWait())
        {
            held = (sequence, payload.ToArray());
            heldArrivals = 0;
            ReorderWaits++;
            return true;
        }
        return Play(sequence, payload);
    }

    /// <summary>Play one packet: it becomes the newest played, and goes to its codec.</summary>
    private bool Play(uint sequence, ReadOnlySpan<byte> payload)
    {
        highestSequence = sequence;
        lateInARow = 0;
        return Codec switch
        {
            AudioTransportCodec.Pcm => HandlePcm(payload),
            AudioTransportCodec.Opus => HandleOpus(sequence, payload),
            AudioTransportCodec.OpusCustom => HandleOpusCustom(sequence, payload),
            _ => false,
        };
    }

    /// <summary>Simulator seam: switch the reorder wait off, to compare with how RemSound was before it.</summary>
    internal static volatile bool ReorderWaitOffForTest;

    private (uint Sequence, byte[] Payload)? held;
    private int heldArrivals;

    /// <summary>Gate seam (the soak and stress steps): a packet held by the reorder wait right now - 0 or 1.</summary>
    internal int HeldForTest => held is null ? 0 : 1;

    private void ReleaseHeld()
    {
        if (held is not { } h) return;
        held = null;
        Play(h.Sequence, h.Payload);
    }

    /// <summary>Whether this stream's buffer holds enough to wait one packet: two frames and a millisecond.</summary>
    private bool CanAffordReorderWait() =>
        sessionPlayout.BufferedMs >= 2 * Math.Max(1, (int)Math.Ceiling(Format.FrameDurationMs)) + 1;

    /// <summary>Packets held by the reorder wait, and of those, how many had their missing packet arrive and slot back
    /// in (the rest were covered as a loss, as always).</summary>
    public long ReorderWaits { get; private set; }
    public long ReorderFilled { get; private set; }

    /// <summary>
    /// Classify each arriving packet against the expected next wire sequence:
    /// IN-ORDER (== expected), MISSING (> expected, diff sample frames), REORDERED (< expected
    /// but within a small sane window), DUPLICATE (== previous). On the very first packet we
    /// just seed expected and bail. On a wild jump (huge gap) we treat it as a re-sync rather
    /// than logging hundreds of thousands of "missing" packets — this can happen if the sender
    /// restarts mid-session or a router drops a long burst.
    /// All counters use Interlocked because the readers are on the UI thread.
    /// </summary>
    private void TrackWireSequence(uint sequence)
    {
        if (expectedNextWireSequence is not uint expected)
        {
            expectedNextWireSequence = sequence + 1U;
            Interlocked.Increment(ref wireInOrderTotal);
            return;
        }

        if (sequence == expected)
        {
            Interlocked.Increment(ref wireInOrderTotal);
            expectedNextWireSequence = sequence + 1U;
            return;
        }

        // Treat the gap as an unsigned forward gap. If it's small-ish (< 1M packets, well over
        // 10 minutes of audio at our packet rates) treat as forward MISSING. If it's huge,
        // assume sequence ran backwards (reorder or restart).
        uint forwardGap = sequence - expected;
        if (forwardGap < 1_000_000U)
        {
            // Forward jump → forwardGap packets we never saw at the expected slot.
            Interlocked.Add(ref wireMissedTotal, forwardGap);
            expectedNextWireSequence = sequence + 1U;
        }
        else
        {
            // Backward jump. Distance behind expected:
            uint backwardDistance = expected - sequence;
            if (backwardDistance == 1U)
            {
                // sequence == previous (the one just before expected) → duplicate.
                Interlocked.Increment(ref wireDuplicatedTotal);
            }
            else
            {
                // Out-of-order arrival from further back.
                Interlocked.Increment(ref wireReorderedTotal);
            }
            // Do NOT roll expectedNextWireSequence backwards — that would re-count the
            // already-missing packets when the originally-expected packet arrives.
        }
    }

    // ---- One stream arriving on two of the peer's addresses ---------------------------------------
    //
    // A peer on the LAN and on a VPN at once can have its packets reach us from either address, and
    // the receiver now folds both onto ONE session rather than opening a second one that splits the
    // stream (see AudioReceiver.SessionEndpointFor). Folding them together raises a question the
    // single-path case never had: if the sender is DUPLICATING to both paths rather than alternating
    // between them, every packet would arrive twice and be decoded twice, and the buffer would fill at
    // double rate. What we saw in the log was alternation, not duplication - but "what we saw once" is
    // not a guarantee, and the failure would be a peer who sounds wrong for reasons nothing reports.
    //
    // So a merged session keeps a 64-packet replay window and drops what it has already had. It is
    // armed ONLY once a second path has actually been seen, so an ordinary session pays one boolean
    // per packet and nothing else.

    private bool multiPath;
    private uint replayHighest;
    private ulong replayMask;

    /// <summary>This stream has now arrived from a second address belonging to the same peer.</summary>
    public void NoteAlternatePath() => multiPath = true;

    /// <summary>Has this stream been seen on more than one of its peer's paths?</summary>
    public bool IsMultiPath => multiPath;

    /// <summary>Have we already delivered this wire sequence? Only ever true for a merged session.
    /// A sequence too far back to judge is let through: being wrong in that direction costs one
    /// packet's worth of doubling, and being wrong the other way silences real audio.</summary>
    private bool AlreadyDelivered(uint sequence)
    {
        if (!multiPath) return false;
        if (replayMask == 0)
        {
            replayHighest = sequence;
            replayMask = 1;
            return false;
        }
        if (sequence == replayHighest) return true;

        var forward = sequence - replayHighest;      // unsigned: wraps correctly
        if (forward < 1_000_000U)
        {
            replayMask = forward >= 64 ? 1UL : (replayMask << (int)forward) | 1UL;
            replayHighest = sequence;
            return false;
        }

        var back = replayHighest - sequence;
        if (back >= 64) return false;
        var bit = 1UL << (int)back;
        if ((replayMask & bit) != 0) return true;
        replayMask |= bit;
        return false;
    }

    public void Dispose()
    {
        // 2026-05-27 — the comment that used to live here said "IOpusDecoder has no Dispose"
        // and that was true for the pure-managed Concentus.OpusDecoder we used pre-v2.2.
        // After Concentus.Native was wired in (v2.2 / shipped in v3.0), the concrete decoder
        // returned by OpusCodecFactory.CreateDecoder is the native-backed NativeOpusDecoder,
        // which IS IDisposable and owns native libopus state. Not calling Dispose here meant
        // the native state only released when the GC eventually finalized the wrapper —
        // which never happened in practice because we set GCSettings.SustainedLowLatency
        // (see Program.Main). Andre's 23-hour receive session showed the resulting working-
        // set climb (83 MB → 3.5 GB). The cast-to-IDisposable handles both the native and
        // the pure-managed path transparently — if the concrete type doesn't implement
        // IDisposable, the as-cast yields null and the null-conditional is a no-op.
        lock (handlingGate)
        {
            if (disposed) return;
            disposed = true;
            (opusDecoder as IDisposable)?.Dispose();
            opusDecoder = null;
            customDecoder?.Dispose();
            customDecoder = null;
        }
    }

    // === PCM ===

    private bool HandlePcm(ReadOnlySpan<byte> payload)
    {
        if (!RemPcmFrame.TryReadSubHeader(payload, out var frameId, out var partIndex, out var totalParts))
        {
            return false;
        }

        var partBytes = payload[RemPcmFrame.SubHeaderSize..];
        if (!pcmAssembler.TryAssemble(partBytes, frameId, partIndex, totalParts, out var assembled))
        {
            return true; // pending or dropped due to mismatch — not an error condition
        }

        // The reassembled frame is ciphertext — decrypt it. An empty result means the peer's
        // password doesn't match ours (or we have no key): drop silently. The app surfaces the
        // mismatch from the fingerprint in the format packet, so it isn't a mystery to the user.
        var assembledPlain = decryptor.TryDecrypt(assembled);
        if (assembledPlain.IsEmpty) return false;

        // assembledPlain is signed int24 LE, stereo. Convert to float32 and queue.
        var sampleCount = assembledPlain.Length / 3;
        var floatBytes = sampleCount * sizeof(float);
        Span<byte> floatScratch = floatBytes <= 16 * 1024 ? stackalloc byte[floatBytes] : new byte[floatBytes];
        var floatSpan = MemoryMarshal.Cast<byte, float>(floatScratch);
        PcmPack.Int24LEToFloat(assembledPlain, floatSpan);

        // Discontinuity probe — what does the audio look like right after we decode it?
        // Compared to the sender's pre-encode probe, a higher value here would mean the
        // wire codec roundtrip introduced steps. Same probe is also useful as a baseline
        // for the post-ring-read probe in SessionPlayout.
        postDecodeStepProbe.ScanStereo(floatSpan);

        // Frames the network lost, made up before this one so the audio stays in time - see ConcealLostPcmFrames.
        var framesPerChannel = sampleCount / Format.Channels;
        if (lastPcmFrameId is uint previousFrame)
        {
            var lost = (long)frameId - previousFrame - 1;
            if (lost is >= 1 and <= MaxPcmFramesConcealed) ConcealLostPcmFrames((int)lost * framesPerChannel, floatSpan);
        }
        lastPcmFrameId = frameId;
        if (Format.Channels == 2 && floatSpan.Length >= 2)
        {
            lastPcmLeft = floatSpan[^2];
            lastPcmRight = floatSpan[^1];
        }

        sessionPlayout.Write(floatScratch);
        onFramesQueued(framesPerChannel);
        return true;
    }

    // === PCM lost-frame concealment (2026-09-28) ===

    /// <summary>The most lost PCM frames made up in one go (about 40 ms at 233-sample frames). A longer gap is a stream
    /// that stopped, and is left to the buffer, as before.</summary>
    internal const int MaxPcmFramesConcealed = 8;

    /// <summary>Frames made up for lost PCM packets, for the gate and the simulator.</summary>
    public long PcmConcealedFrames { get; private set; }

    private uint? lastPcmFrameId;
    private float lastPcmLeft, lastPcmRight;

    /// <summary>
    /// A lost PCM packet left no samples behind, so the buffer's clock-matching - samples written against samples played,
    /// over ten seconds - saw the sender as slow by the loss and played everything that much slower: 0.5 % lost, about 9
    /// cents flat; 1 %, 17 to 23, on the ASIO output and the WASAPI one alike (found by the simulator, 2026-09-27). Opus
    /// never had it: its repair writes a frame for every lost one. This does the same for PCM: the missing time is written,
    /// the last sample fading to silence over a millisecond and then silence, and the frame that did arrive fades back in,
    /// so the loss is the same short gap it always was - only now in time and in tune.
    /// </summary>
    private void ConcealLostPcmFrames(int frames, Span<float> arrived)
    {
        const int Fade = 48; // 1 ms at 48 kHz
        var made = new float[frames * 2];
        var fadeOut = Math.Min(Fade, frames);
        for (var i = 0; i < fadeOut; i++)
        {
            var g = (float)(0.5 * (1 + Math.Cos(Math.PI * (i + 1) / fadeOut)));
            made[2 * i] = lastPcmLeft * g;
            made[2 * i + 1] = lastPcmRight * g;
        }
        sessionPlayout.Write(MemoryMarshal.AsBytes(made.AsSpan()));
        onFramesQueued(frames);
        PcmConcealedFrames += frames;
        var fadeIn = Math.Min(Fade, arrived.Length / 2);
        for (var i = 0; i < fadeIn; i++)
        {
            var g = (float)(0.5 * (1 - Math.Cos(Math.PI * (i + 1) / fadeIn)));
            arrived[2 * i] *= g;
            arrived[2 * i + 1] *= g;
        }
    }

    // === Opus ===

    /// <summary>
    /// Decode one Opus packet, using inband FEC to fill a single-packet gap when we see one.
    ///
    /// <para><b>Sequence bookkeeping runs on EVERY exit path.</b> It used to be a single assignment
    /// at the very end, reached only after a fully successful decode — so a packet dropped for any
    /// other reason (no decoder, decryption failed because the passwords differ, libopus threw, a
    /// zero-length decode) left <c>expectedNextSequence</c> stale. The first good packet afterwards
    /// then measured its gap against a stale expectation: during a password mismatch that is
    /// thousands of packets, and when the password is corrected the recovery is counted as one huge
    /// unrecovered gap. Worse, if the FEC decode succeeded and the real decode then threw, the same
    /// gap was detected again on the next packet and the recovered frame was emitted TWICE.
    /// 2026-08-23 audit, finding R6.</para>
    /// </summary>
    private bool HandleOpus(uint sequence, ReadOnlySpan<byte> payload)
    {
        // Whatever happens below, this packet's sequence is now the reference point. Set it up front
        // so no early return can leave the gap detector reading from a stale one.
        var previousExpected = expectedNextSequence;
        expectedNextSequence = sequence + 1U;

        if (opusDecoder is null) return false;

        // Decrypt the Opus payload up front; both the FEC pass and the normal decode below use
        // the plaintext. An empty result = wrong password / no key set → drop (silence). The
        // mismatch is surfaced to the user from the format-packet fingerprint. 2026-05-31.
        payload = decryptor.TryDecrypt(payload);
        if (payload.IsEmpty) return false;

        // Frame size in samples-per-channel comes directly off the wire in v3.0+ (was
        // SampleRate × ms / 1000 in v2.x). Floor at 120 = 2.5 ms = standard libopus
        // RESTRICTED_LOWDELAY minimum, so a malformed format packet with a tiny value can't
        // size the scratch buffer below the encoder's minimum frame size.
        var frameSize = Math.Max(120, Format.FrameSamplesPerChannel);
        var totalShorts = frameSize * Format.Channels;
        Span<short> shortScratch = totalShorts <= 4096 ? stackalloc short[totalShorts] : new short[totalShorts];

        // Detect a single-packet gap. If the previous packet was N and this is N+2,
        // we know N+1 was lost; this packet's payload contains FEC redundancy for
        // it. Decode the FEC frame first (so audio plays in order), then the
        // current frame. Wrap-around with uint subtraction is intentional.
        bool useFecRecovery = false;
        if (previousExpected is uint expected)
        {
            uint gap = sequence - expected; // 0 = exactly expected, 1 = one missing, 2+ = multi-loss
            if (gap == 1)
            {
                useFecRecovery = true;
            }
            else if (gap > 1 && gap < 1_000_000)
            {
                // Multi-packet loss — FEC can only recover one. Don't try.
                OpusUnrecoveredGaps++;
            }
            // gap == 0 OR a wild jump (gap >= 1M, e.g. stream reset) → no recovery
        }

        if (useFecRecovery)
        {
            try
            {
                var fecDecoded = opusDecoder.Decode(payload, shortScratch, frameSize, true);
                if (fecDecoded > 0)
                {
                    EmitDecoded(shortScratch, fecDecoded);
                    OpusFecRecoveries++;
                }
            }
            catch
            {
                // FEC recovery is best-effort; if it fails, fall through to the
                // normal decode and accept a single click rather than crashing.
            }
        }

        int decoded;
        try
        {
            decoded = opusDecoder.Decode(payload, shortScratch, frameSize, false);
        }
        catch
        {
            return false;
        }
        if (decoded <= 0) return false;

        EmitDecoded(shortScratch, decoded);
        return true; // expectedNextSequence was advanced at the top — see the method summary.
    }

    private void EmitDecoded(ReadOnlySpan<short> shortScratch, int sampleCountPerChannel)
    {
        var floatCount = sampleCountPerChannel * Format.Channels;
        var floatBytes = floatCount * sizeof(float);
        Span<byte> floatScratch = floatBytes <= 16 * 1024 ? stackalloc byte[floatBytes] : new byte[floatBytes];
        var floatSpan = MemoryMarshal.Cast<byte, float>(floatScratch);
        for (var i = 0; i < floatCount; i++) floatSpan[i] = shortScratch[i] / 32768f;
        // The post-decode step probe, fed on the Opus path too: its log columns read 0 for every Opus stream, and Opus is
        // the codec most people send. 2026-09-13 review.
        postDecodeStepProbe.ScanStereo(floatSpan);

        sessionPlayout.Write(floatScratch);
        onFramesQueued(sampleCountPerChannel);
    }
}
