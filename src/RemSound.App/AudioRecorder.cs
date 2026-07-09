using System.Diagnostics;
using Concentus;
using Concentus.Enums;
using Concentus.Oggfile;
using CUETools.Codecs;
using CUETools.Codecs.FLAKE;
using NAudio.Lame;
using NAudio.Wave;
using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// Background recorder that writes float audio to disk as WAV (custom PCM writer with
/// crash-resilient header refresh), MP3 (LAME via NAudio.Lame), OGG-Opus (Concentus +
/// Concentus.Oggfile), or FLAC (CUETools.Codecs.FLAKE — pure managed lossless).
///
/// Pipeline:
///   1. Sender / receiver audio threads call <see cref="WriteSent"/> /
///      <see cref="WriteReceived"/> — each appends to a pre-allocated lock-free SPSC ring
///      buffer (one per direction) using nothing but a memcpy, an atomic add on the write
///      head, and an event Set. Zero allocations, zero locks, zero signaling primitives
///      that could contend with disk I/O. Audio threads NEVER touch the disk and never
///      touch the file writers.
///   2. A single background writer thread waits on the wake-up event, drains both rings,
///      mixes the two directions when source mode is "Both", and feeds the resulting
///      samples to the format writer.
///   3. <see cref="Stop"/> drains anything still in the rings, closes the file, and
///      signals the caller with the final path and byte count.
///
/// This shape replaced an earlier BlockingCollection + ArrayPool design (2026-05-14)
/// that exhibited intermittent pops under priority mode + recording. The semaphore
/// signaling inside BlockingCollection and the per-call ArrayPool rents were both
/// occasional sources of multi-hundred-microsecond audio-thread spikes; with a
/// 32-sample ASIO buffer (0.67 ms callback budget) that was enough to miss deadlines.
/// The lock-free ring keeps audio-thread work bounded to a handful of nanoseconds.
///
/// "Both" source mode: when both rings have audio, the writer thread drains
/// min(sent_avail, received_avail) frames and sum-mixes them. When only one side has
/// data (e.g. the user has Send Audio off, or no peer is connected), that side is
/// drained solo with the other treated as silence — the recording never stalls because
/// of a quiet direction.
///
/// Channel-mode downmix happens at the writer-thread layer (one place to do it cleanly)
/// rather than at each enqueue point.
///
/// Lifecycle: one AudioRecorder per recording session. The MainForm creates a fresh one
/// on Start and disposes it on Stop. Reconfiguring mid-session is not supported — the user
/// stops, edits settings, and starts again.
/// </summary>
internal sealed class AudioRecorder : IDisposable
{
    private const int MixSampleRate = 48000;
    private const int MixChannels = 2;

    /// <summary>Per-direction ring capacity in floats. 5 s of stereo float @ 48 kHz =
    /// 480 000 floats ≈ 1.9 MB. Sized to cover any reasonable disk hiccup; in steady
    /// state the rings hover near empty because the writer drains continuously. Two
    /// rings means ~3.8 MB of fixed-cost memory per running recording — modest.</summary>
    private const int RingCapacityFloats = MixSampleRate * MixChannels * 5;

    /// <summary>Minimum frames the writer waits for before doing a drain pass. 480 frames
    /// = 10 ms of audio. Below this, signaling overhead dominates; above this, the
    /// chunks are big enough that a single Write to the file format writer is efficient.
    /// Also caps the latency between an audio thread's tap and the disk write at ~10 ms.</summary>
    private const int DrainChunkFrames = 480;

    /// <summary>Maximum frames the writer drains in a single Process call. Caps the
    /// CPU burst on the writer thread when the rings have been allowed to fill (e.g.
    /// after a brief disk stall). At 4800 frames = 100 ms of audio per Process, the
    /// writer can still keep up with a 5 s ring (50 Process calls to drain it fully).</summary>
    private const int DrainChunkMaxFrames = 4800;

    private readonly RecordingSettings settings;
    private readonly string resolvedPath;
    private readonly Action<string>? onDiagnostic;
    private readonly Action<string, long>? onFinished;

    // === Lock-free SPSC rings, per direction × per lane ===
    // Write head is monotonically increasing (NOT wrapped). Ring index = head % capacity.
    // This avoids the ABA problem on wraparound and means the audio thread only needs an
    // atomic add (not a CAS) to publish a write. The writer thread holds the read head
    // (no atomic needed; single consumer).
    //
    // Four rings rather than two so the writer can correctly handle BothIndependent mode
    // where the PlayoutEngine's per-lane Read fires from BOTH the WASAPI lane and the ASIO
    // lane independently. Pre-2026-05-15 the recorder had a single ring per direction and
    // both lanes' samples got appended sequentially — the file ended up with twice the
    // expected audio at half the wall-clock duration, garbled because the two lanes' content
    // was different.
    //
    // Lane mapping:
    //   * RenderRoute.WasapiLane → wasapi slot
    //   * RenderRoute.AsioLane   → asio slot
    //   * RenderRoute.Mixed      → wasapi slot (classic modes have only one tap firing, so
    //                              the asio slot stays empty — no double-up)
    //
    // The writer thread reads from both slots per direction and:
    //   * mixes them when both have data (BothIndependent mode with both output lanes active),
    //   * drains whichever solo lane has data when only one is firing (classic modes, or
    //     BothIndependent with only one lane's output ticked).
    private readonly float[] sentWasapiRing = new float[RingCapacityFloats];
    private readonly float[] sentAsioRing = new float[RingCapacityFloats];
    private readonly float[] receivedWasapiRing = new float[RingCapacityFloats];
    private readonly float[] receivedAsioRing = new float[RingCapacityFloats];
    private long sentWasapiWriteHead;
    private long sentWasapiReadHead;
    private long sentAsioWriteHead;
    private long sentAsioReadHead;
    private long receivedWasapiWriteHead;
    private long receivedWasapiReadHead;
    private long receivedAsioWriteHead;
    private long receivedAsioReadHead;
    private long droppedSampleFrames;

    // Wake-up event. Audio threads Set after appending to a ring; writer thread Waits.
    // ManualResetEventSlim has a Spin phase before falling back to a kernel wait, so
    // light contention stays in user-mode and is cheap.
    private readonly ManualResetEventSlim wakeup = new(initialState: false, spinCount: 32);
    private readonly Thread writerThread;
    private readonly CancellationTokenSource cts = new();
    private long writtenSampleFrames;
    private long writtenBytes;
    private volatile bool stopped;

    public string FilePath => resolvedPath;
    public RecordingSettings Settings => settings;
    public long WrittenSampleFrames => Interlocked.Read(ref writtenSampleFrames);

    /// <summary>Total stereo frames the audio thread had to drop because its ring was
    /// full. Non-zero indicates the writer can't keep up with the audio rate — usually
    /// a sign of a stalled disk. Surfaced in the on-stop diagnostic line.</summary>
    public long DroppedSampleFrames => Interlocked.Read(ref droppedSampleFrames);

    /// <summary>Constructs the recorder, opens the output file, and starts the writer
    /// thread. If anything fails the constructor throws and no cleanup is needed (no
    /// file has been opened yet).</summary>
    public AudioRecorder(RecordingSettings settings, Action<string>? onDiagnostic, Action<string, long>? onFinished, string? explicitPath = null)
    {
        this.settings = settings.Clone();
        this.onDiagnostic = onDiagnostic;
        this.onFinished = onFinished;

        if (explicitPath is not null)
        {
            // The RecordingController drives the folder/file naming (date folders, per-peer split
            // tracks). Honour the path it hands us, creating the containing folder.
            resolvedPath = explicitPath;
            Directory.CreateDirectory(Path.GetDirectoryName(explicitPath) ?? RecordingSettings.DefaultFolder());
        }
        else
        {
            var folder = settings.ResolvedFolder();
            if (string.IsNullOrWhiteSpace(folder)) folder = RecordingSettings.DefaultFolder();
            Directory.CreateDirectory(folder);

            var ext = ExtensionFor(settings.FileFormat);
            var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            resolvedPath = Path.Combine(folder, $"RemSound-{stamp}.{ext}");
        }

        // Writer creation happens on the constructor thread so any open errors are surfaced
        // synchronously to the caller.
        formatWriter = CreateWriter(settings.FileFormat, resolvedPath, settings);

        // Writer thread at Normal priority. Previously AboveNormal, lowered 2026-05-14:
        // there's no reason for the writer to compete with audio threads (which run at
        // MMCSS Pro Audio priority anyway, well above any "Normal" worker). Keeping the
        // writer at Normal lets the OS scheduler push it out of the way whenever the
        // audio thread needs the CPU.
        writerThread = new Thread(WriterLoop)
        {
            IsBackground = true,
            Name = "RemSound.Recorder",
            Priority = ThreadPriority.Normal,
        };
        writerThread.Start();
    }

    // === Audio-thread side: bounded to a memcpy + atomic add + event-set ===

    /// <summary>Tap target for sender-side audio. Discarded silently if this recorder's
    /// source mode is "received only". The <paramref name="lane"/> identifies which
    /// SenderLane the samples came from so the writer thread can keep WASAPI-lane and
    /// ASIO-lane streams separate (and mix them at drain time). RenderRoute.Mixed (the
    /// classic-mode case) routes to the WASAPI slot as the canonical "single lane".
    /// Lock-free, allocation-free; safe to call from the audio thread.</summary>
    public void WriteSent(ReadOnlyMemory<float> stereoFloats, RenderRoute lane)
    {
        if (stopped) return;
        if (settings.Source == RecordingSource.ReceivedOnly) return;
        if (lane == RenderRoute.AsioLane)
        {
            AppendToRing(stereoFloats.Span, sentAsioRing, ref sentAsioWriteHead, ref sentAsioReadHead);
        }
        else
        {
            // WasapiLane and Mixed both land in the wasapi slot. In classic modes only
            // this slot fires; in BothIndependent the WASAPI lane fires here and the ASIO
            // lane fires in the asio slot above.
            AppendToRing(stereoFloats.Span, sentWasapiRing, ref sentWasapiWriteHead, ref sentWasapiReadHead);
        }
    }

    /// <summary>Tap target for receiver-side audio. Discarded silently if this recorder's
    /// source mode is "sent only". <paramref name="lane"/> tags which PlayoutEngine
    /// per-lane Read invoked us — same RenderRoute mapping as <see cref="WriteSent"/>.
    /// Lock-free, allocation-free; safe to call from the render thread.</summary>
    public void WriteReceived(ReadOnlyMemory<float> stereoFloats, RenderRoute lane)
    {
        if (stopped) return;
        if (settings.Source == RecordingSource.SentOnly) return;
        if (lane == RenderRoute.AsioLane)
        {
            AppendToRing(stereoFloats.Span, receivedAsioRing, ref receivedAsioWriteHead, ref receivedAsioReadHead);
        }
        else
        {
            AppendToRing(stereoFloats.Span, receivedWasapiRing, ref receivedWasapiWriteHead, ref receivedWasapiReadHead);
        }
    }

    /// <summary>Lock-free, allocation-free append to a single-producer-single-consumer
    /// ring buffer. The producer (audio thread) owns the write head; the consumer (writer
    /// thread) owns the read head. The producer reads BOTH heads (Volatile.Read) to
    /// compute available space; the consumer reads BOTH heads similarly. Cross-thread
    /// visibility is provided by Volatile.Read/Write — sufficient for x86/x64 memory
    /// model on Windows and the only platform we target.</summary>
    private void AppendToRing(ReadOnlySpan<float> samples, float[] ring, ref long writeHeadRef, ref long readHeadRef)
    {
        var len = samples.Length;
        if (len == 0) return;
        var cap = ring.Length;
        var write = Volatile.Read(ref writeHeadRef);
        var read = Volatile.Read(ref readHeadRef);
        var used = (int)(write - read);
        var free = cap - used;
        if (free < len)
        {
            // Ring is full. Audio thread can't block (deadline-bound); we drop these
            // samples and bump the counter. In practice this fires only if the writer
            // thread is genuinely stuck (very slow disk, OS hang).
            Interlocked.Add(ref droppedSampleFrames, len / MixChannels);
            return;
        }
        var pos = (int)(write % cap);
        var part1 = Math.Min(len, cap - pos);
        samples.Slice(0, part1).CopyTo(ring.AsSpan(pos));
        if (part1 < len)
        {
            // Wrap-around: copy the tail into the start of the ring.
            samples.Slice(part1).CopyTo(ring.AsSpan(0));
        }
        // Publish the write — Volatile.Write ensures the sample writes above are visible
        // to the consumer BEFORE it sees the advanced write head.
        Volatile.Write(ref writeHeadRef, write + len);
        // Wake the writer. ManualResetEventSlim.Set is a single Interlocked.CompareExchange
        // on the fast path; subsequent Sets while the event is already set are essentially
        // free.
        wakeup.Set();
    }

    // === Writer thread: drains both rings, mixes if "Both", writes to file ===

    private void WriterLoop()
    {
        try
        {
            while (!cts.IsCancellationRequested)
            {
                // Block until the audio thread signals data OR we time out (the timeout is
                // a backstop so periodic format-writer flushes still happen during a long
                // silent stretch with no incoming audio).
                wakeup.Wait(50, cts.Token);
                wakeup.Reset();

                // Drain as much as is available, in chunks of up to DrainChunkMaxFrames.
                while (!cts.IsCancellationRequested && HasEnoughData())
                {
                    Process();
                }
            }
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
        catch (Exception ex)
        {
            onDiagnostic?.Invoke($"recording: writer-thread error: {ex.GetType().Name}: {ex.Message}");
        }

        // Final drain on shutdown: anything still queued in the rings goes to disk before
        // we close the file.
        try
        {
            while (HasEnoughData(minFrames: 1)) Process();
        }
        catch { /* shutdown drain is best-effort */ }
    }

    private bool HasEnoughData(int minFrames = DrainChunkFrames)
    {
        var sentWasapi = (Volatile.Read(ref sentWasapiWriteHead) - sentWasapiReadHead) / MixChannels;
        var sentAsio = (Volatile.Read(ref sentAsioWriteHead) - sentAsioReadHead) / MixChannels;
        var recvWasapi = (Volatile.Read(ref receivedWasapiWriteHead) - receivedWasapiReadHead) / MixChannels;
        var recvAsio = (Volatile.Read(ref receivedAsioWriteHead) - receivedAsioReadHead) / MixChannels;
        // "Any frame in this direction" check — the per-lane drain helper handles the
        // mix-vs-solo decision at process time, so for the wakeup heuristic we just need to
        // know SOMETHING is waiting in the direction(s) we care about.
        var sentAvail = sentWasapi + sentAsio;
        var recvAvail = recvWasapi + recvAsio;
        return settings.Source switch
        {
            RecordingSource.SentOnly => sentAvail >= minFrames,
            RecordingSource.ReceivedOnly => recvAvail >= minFrames,
            RecordingSource.Both => sentAvail >= minFrames || recvAvail >= minFrames,
            _ => false,
        };
    }

    /// <summary>Drain one direction worth of audio into <paramref name="dst"/>, merging the
    /// WASAPI-lane and ASIO-lane rings into a single stream. Behaviour:
    ///   * Both lanes have frames available: drain <c>min(wasapi, asio, maxFrames)</c>,
    ///     sum-mix with a soft-tanh limiter on the sum (same pattern as the cross-direction
    ///     "Both" mode mix downstream).
    ///   * Only one lane has frames: drain it solo into dst (the inactive lane contributes
    ///     nothing this tick).
    ///   * Neither lane has frames: return 0; caller skips this direction.
    /// Returns the number of stereo frames written into dst.
    ///
    /// The <paramref name="aux"/> span must be at least dst.Length floats; it's used as the
    /// staging area for the second lane during a both-lane mix and is otherwise unused.</summary>
    private static int DrainOneDirection(
        float[] wasapiRing, ref long wasapiWriteHead, ref long wasapiReadHead,
        float[] asioRing, ref long asioWriteHead, ref long asioReadHead,
        Span<float> dst, Span<float> aux, int maxFrames)
    {
        var wasapiAvail = (int)((Volatile.Read(ref wasapiWriteHead) - wasapiReadHead) / MixChannels);
        var asioAvail = (int)((Volatile.Read(ref asioWriteHead) - asioReadHead) / MixChannels);

        if (wasapiAvail > 0 && asioAvail > 0)
        {
            var frames = Math.Min(Math.Min(wasapiAvail, asioAvail), maxFrames);
            if (frames <= 0) return 0;
            var len = frames * MixChannels;
            CopyFromRing(wasapiRing, ref wasapiReadHead, dst.Slice(0, len));
            CopyFromRing(asioRing, ref asioReadHead, aux.Slice(0, len));
            // Sum + soft-tanh limit. Two BothIndependent lanes routinely carry different
            // content (each lane is its own peer-stream selection), so summing is the right
            // mix; the limiter prevents two simultaneously-hot lanes from clipping the file.
            for (var i = 0; i < len; i++)
            {
                var s = dst[i] + aux[i];
                if (s > 1f) s = 1f - MathF.Tanh(s - 1f);
                else if (s < -1f) s = -1f + MathF.Tanh(-1f - s);
                dst[i] = s;
            }
            return frames;
        }
        if (wasapiAvail > 0)
        {
            var frames = Math.Min(wasapiAvail, maxFrames);
            if (frames <= 0) return 0;
            CopyFromRing(wasapiRing, ref wasapiReadHead, dst.Slice(0, frames * MixChannels));
            return frames;
        }
        if (asioAvail > 0)
        {
            var frames = Math.Min(asioAvail, maxFrames);
            if (frames <= 0) return 0;
            CopyFromRing(asioRing, ref asioReadHead, dst.Slice(0, frames * MixChannels));
            return frames;
        }
        return 0;
    }

    /// <summary>How many frames <see cref="DrainOneDirection"/> would produce for one direction right
    /// now, WITHOUT consuming anything — the lane-merge min (or the single active lane), capped at
    /// <paramref name="maxFrames"/>. Used by the Both path to drain the sent and received directions by
    /// the SAME amount so neither ring is over-consumed and the two stay sample-aligned. Read heads are
    /// owned by this (writer) thread so a plain read is fine; the caller passes Volatile.Read snapshots
    /// of the write heads, which the audio threads advance.</summary>
    private static int DirectionAvailFrames(long wasapiWrite, long wasapiRead, long asioWrite, long asioRead, int maxFrames)
    {
        var w = (int)((wasapiWrite - wasapiRead) / MixChannels);
        var a = (int)((asioWrite - asioRead) / MixChannels);
        int avail;
        if (w > 0 && a > 0) avail = Math.Min(w, a);
        else if (w > 0) avail = w;
        else if (a > 0) avail = a;
        else avail = 0;
        return Math.Min(avail, maxFrames);
    }

    private void Process()
    {
        int framesThisCall;
        switch (settings.Source)
        {
            case RecordingSource.SentOnly:
                // One-shot scratch sizing — start big enough for the chunk cap so we don't
                // resize per call. The actual write may be smaller depending on per-lane
                // availability.
                EnsureScratchSize(DrainChunkMaxFrames * MixChannels);
                EnsureSecondaryScratchSize(DrainChunkMaxFrames * MixChannels);
                framesThisCall = DrainOneDirection(
                    sentWasapiRing, ref sentWasapiWriteHead, ref sentWasapiReadHead,
                    sentAsioRing, ref sentAsioWriteHead, ref sentAsioReadHead,
                    mixScratch, mixScratchAux, DrainChunkMaxFrames);
                if (framesThisCall <= 0) return;
                EmitMixBuffer(framesThisCall);
                break;

            case RecordingSource.ReceivedOnly:
                EnsureScratchSize(DrainChunkMaxFrames * MixChannels);
                EnsureSecondaryScratchSize(DrainChunkMaxFrames * MixChannels);
                framesThisCall = DrainOneDirection(
                    receivedWasapiRing, ref receivedWasapiWriteHead, ref receivedWasapiReadHead,
                    receivedAsioRing, ref receivedAsioWriteHead, ref receivedAsioReadHead,
                    mixScratch, mixScratchAux, DrainChunkMaxFrames);
                if (framesThisCall <= 0) return;
                EmitMixBuffer(framesThisCall);
                break;

            case RecordingSource.Both:
                // Two-stage drain. First produce a per-direction stream for each direction
                // (lane-mixed if both lanes have data), then sum-mix the two directions just
                // like the pre-2026-05-15 Both path did. The lane mix uses mixScratchAux as
                // its workspace; the cross-direction mix uses mixScratch (sent) + a per-call
                // received scratch we'll grow as needed.
                EnsureScratchSize(DrainChunkMaxFrames * MixChannels);
                EnsureSecondaryScratchSize(DrainChunkMaxFrames * MixChannels);
                EnsureRecvDirectionScratchSize(DrainChunkMaxFrames * MixChannels);

                // Peek how much each direction can supply WITHOUT consuming, so when both carry audio we
                // drain them by the SAME amount and keep them sample-aligned. Draining each independently
                // (the old code) advanced the faster direction's ring head past frames we then never
                // wrote — a silent, continuous loss + progressive drift on any real two-way session,
                // which is exactly what Both mode exists to capture. Now the surplus genuinely stays in
                // its ring for the next pass.
                var sentAvail = DirectionAvailFrames(
                    Volatile.Read(ref sentWasapiWriteHead), sentWasapiReadHead,
                    Volatile.Read(ref sentAsioWriteHead), sentAsioReadHead, DrainChunkMaxFrames);
                var recvAvail = DirectionAvailFrames(
                    Volatile.Read(ref receivedWasapiWriteHead), receivedWasapiReadHead,
                    Volatile.Read(ref receivedAsioWriteHead), receivedAsioReadHead, DrainChunkMaxFrames);

                if (sentAvail > 0 && recvAvail > 0)
                {
                    // Both directions have data — take the same count from each. Because write heads only
                    // advance (producers add) and this is the sole consumer, each drain returns exactly
                    // `take`, so the two stay aligned and nothing is over-consumed.
                    var take = Math.Min(sentAvail, recvAvail);
                    var got1 = DrainOneDirection(
                        sentWasapiRing, ref sentWasapiWriteHead, ref sentWasapiReadHead,
                        sentAsioRing, ref sentAsioWriteHead, ref sentAsioReadHead,
                        mixScratch, mixScratchAux, take);
                    var got2 = DrainOneDirection(
                        receivedWasapiRing, ref receivedWasapiWriteHead, ref receivedWasapiReadHead,
                        receivedAsioRing, ref receivedAsioWriteHead, ref receivedAsioReadHead,
                        recvDirectionScratch, mixScratchAux, take);
                    framesThisCall = Math.Min(got1, got2); // defensive; both equal `take` in practice
                    if (framesThisCall <= 0) return;
                    var dst = mixScratch.AsSpan(0, framesThisCall * MixChannels);
                    var aux = recvDirectionScratch.AsSpan(0, framesThisCall * MixChannels);
                    for (var i = 0; i < dst.Length; i++)
                    {
                        var s = dst[i] + aux[i];
                        if (s > 1f) s = 1f - MathF.Tanh(s - 1f);
                        else if (s < -1f) s = -1f + MathF.Tanh(-1f - s);
                        dst[i] = s;
                    }
                }
                else if (sentAvail > 0)
                {
                    // Only the sent direction has audio right now — record it solo (no over-consume,
                    // nothing to align against). mixScratch already holds it.
                    framesThisCall = DrainOneDirection(
                        sentWasapiRing, ref sentWasapiWriteHead, ref sentWasapiReadHead,
                        sentAsioRing, ref sentAsioWriteHead, ref sentAsioReadHead,
                        mixScratch, mixScratchAux, DrainChunkMaxFrames);
                    if (framesThisCall <= 0) return;
                }
                else if (recvAvail > 0)
                {
                    framesThisCall = DrainOneDirection(
                        receivedWasapiRing, ref receivedWasapiWriteHead, ref receivedWasapiReadHead,
                        receivedAsioRing, ref receivedAsioWriteHead, ref receivedAsioReadHead,
                        recvDirectionScratch, mixScratchAux, DrainChunkMaxFrames);
                    if (framesThisCall <= 0) return;
                    // The recv-direction audio lives in recvDirectionScratch; copy into mixScratch so
                    // EmitMixBuffer (which reads from mixScratch) sees it.
                    var len = framesThisCall * MixChannels;
                    recvDirectionScratch.AsSpan(0, len).CopyTo(mixScratch.AsSpan(0, len));
                }
                else
                {
                    return;
                }
                EmitMixBuffer(framesThisCall);
                break;

            default:
                return;
        }
    }

    /// <summary>Copy <paramref name="dst"/>.Length floats from <paramref name="ring"/>
    /// starting at <paramref name="readHeadRef"/>, advancing the head atomically.</summary>
    private static void CopyFromRing(float[] ring, ref long readHeadRef, Span<float> dst)
    {
        var len = dst.Length;
        var cap = ring.Length;
        var read = readHeadRef;
        var pos = (int)(read % cap);
        var part1 = Math.Min(len, cap - pos);
        ring.AsSpan(pos, part1).CopyTo(dst);
        if (part1 < len)
        {
            ring.AsSpan(0, len - part1).CopyTo(dst.Slice(part1));
        }
        // Publish the consumed bytes — Volatile.Write so the producer (audio thread)
        // sees the freed slots before its next free-space calculation.
        Volatile.Write(ref readHeadRef, read + len);
    }

    private void EmitMixBuffer(int frames)
    {
        var src = mixScratch.AsSpan(0, frames * MixChannels);
        if (settings.ChannelMode == RecordingChannelMode.Mono)
        {
            EnsureMonoScratchSize(frames);
            for (var i = 0; i < frames; i++)
            {
                monoScratch[i] = (src[i * 2] + src[i * 2 + 1]) * 0.5f;
            }
            formatWriter?.Write(monoScratch.AsSpan(0, frames));
            Interlocked.Add(ref writtenSampleFrames, frames);
        }
        else
        {
            formatWriter?.Write(src);
            Interlocked.Add(ref writtenSampleFrames, frames);
        }
    }

    private void EnsureScratchSize(int floats)
    {
        if (mixScratch.Length < floats) mixScratch = new float[floats];
    }

    private void EnsureSecondaryScratchSize(int floats)
    {
        if (mixScratchAux.Length < floats) mixScratchAux = new float[floats];
    }

    private void EnsureRecvDirectionScratchSize(int floats)
    {
        if (recvDirectionScratch.Length < floats) recvDirectionScratch = new float[floats];
    }

    private void EnsureMonoScratchSize(int frames)
    {
        if (monoScratch.Length < frames) monoScratch = new float[frames];
    }

    /// <summary>Stops the recorder. Drains any audio still in the rings, closes the file,
    /// and signals the finish callback with the path + byte count. Safe to call multiple
    /// times.</summary>
    public void Stop()
    {
        if (stopped) return;
        stopped = true;
        cts.Cancel();
        wakeup.Set();
        try
        {
            writerThread?.Join(TimeSpan.FromSeconds(3));
        }
        catch { /* don't propagate join failures */ }
        try
        {
            formatWriter?.Dispose();
        }
        catch (Exception ex)
        {
            onDiagnostic?.Invoke($"recording: format-writer close failed: {ex.GetType().Name}: {ex.Message}");
        }
        formatWriter = null;
        try
        {
            var fi = new FileInfo(resolvedPath);
            if (fi.Exists)
            {
                writtenBytes = fi.Length;
            }
        }
        catch { /* file-size lookup failure is benign */ }
        if (DroppedSampleFrames > 0)
        {
            onDiagnostic?.Invoke($"recording: dropped {DroppedSampleFrames} stereo frames due to writer back-pressure");
        }
        onFinished?.Invoke(resolvedPath, writtenBytes);
    }

    public void Dispose()
    {
        try { Stop(); } catch { /* shutdown is best-effort */ }
        cts.Dispose();
        wakeup.Dispose();
    }

    // === format-writer plumbing ===
    private IFormatWriter? formatWriter;
    private float[] mixScratch = new float[DrainChunkFrames * MixChannels];
    private float[] mixScratchAux = new float[DrainChunkFrames * MixChannels];
    // Holds the per-direction "received" mix during a Both-source iteration, kept separate
    // from mixScratch (which holds "sent") so the cross-direction final mix can read both
    // simultaneously without one stomping the other.
    private float[] recvDirectionScratch = new float[DrainChunkFrames * MixChannels];
    private float[] monoScratch = new float[DrainChunkFrames];

    public static string ExtensionFor(RecordingFileFormat format) => format switch
    {
        RecordingFileFormat.Wav => "wav",
        RecordingFileFormat.Mp3 => "mp3",
        RecordingFileFormat.Ogg => "opus",  // OGG container, Opus codec — ".opus" is the conventional ext
        RecordingFileFormat.Flac => "flac",
        _ => "wav",
    };

    private static IFormatWriter CreateWriter(RecordingFileFormat format, string path, RecordingSettings settings)
    {
        var channels = settings.ChannelMode == RecordingChannelMode.Mono ? 1 : MixChannels;
        return format switch
        {
            RecordingFileFormat.Wav => new WavFormatWriter(path, MixSampleRate, channels, settings.WavBitsPerSample),
            RecordingFileFormat.Mp3 => new Mp3FormatWriter(path, MixSampleRate, channels, settings.Mp3BitrateKbps),
            RecordingFileFormat.Ogg => new OggOpusFormatWriter(path, MixSampleRate, channels, settings.OggOpusBitrateKbps),
            RecordingFileFormat.Flac => new FlacFormatWriter(path, MixSampleRate, channels, settings.FlacBitsPerSample, settings.FlacCompressionLevel),
            // Defensive: unknown format → WAV (shouldn't happen since all enum members are
            // handled above, but keeps the switch exhaustive).
            _ => new WavFormatWriter(path, MixSampleRate, channels, settings.WavBitsPerSample),
        };
    }


    private interface IFormatWriter : IDisposable
    {
        void Write(ReadOnlySpan<float> samples);
    }

    /// <summary>WAV writer with crash-resilient periodic header updates.
    ///
    /// NAudio's stock WaveFileWriter writes the RIFF / data-chunk size fields ONCE at file
    /// close (in Dispose), with placeholder zeros up until then. A process crash before
    /// Dispose runs leaves the file with header-says-zero-samples, which most players
    /// either refuse or stop after the first audio frame — meaning an hour-long crashed
    /// session is unrecoverable. This implementation owns the FileStream directly and
    /// re-patches the two size fields every <see cref="HeaderRefreshSeconds"/> seconds
    /// PLUS on Dispose. A crash any time after the first refresh leaves a playable WAV
    /// containing all the audio captured up to the last refresh.
    ///
    /// Header layout (PCM 16/24-bit):
    ///   offset  0  "RIFF"
    ///   offset  4  uint32  (file size - 8)            ← patched periodically
    ///   offset  8  "WAVE"
    ///   offset 12  "fmt "
    ///   offset 16  uint32  16  (PCM fmt chunk size)
    ///   offset 20  uint16  1   (PCM format code)
    ///   offset 22  uint16  channels
    ///   offset 24  uint32  sample rate
    ///   offset 28  uint32  byte rate
    ///   offset 32  uint16  block align
    ///   offset 34  uint16  bits per sample
    ///   offset 36  "data"
    ///   offset 40  uint32  data chunk size            ← patched periodically
    ///   offset 44  audio samples...
    ///
    /// For 32-bit IEEE float we use the slightly-longer 18-byte fmt chunk variant with
    /// format code 3 and a trailing cbSize=0 field, so the data chunk starts at offset 46.
    /// </summary>
    private sealed class WavFormatWriter : IFormatWriter
    {
        private const int HeaderRefreshSeconds = 5;

        private readonly FileStream stream;
        private readonly int bitsPerSample;
        private readonly bool isFloat;
        private readonly long dataChunkSizeFieldPos;
        private readonly long dataStartPos;
        private long dataBytesWritten;
        private DateTime lastHeaderRefreshUtc;
        private byte[] scratchBytes = new byte[4096];

        public WavFormatWriter(string path, int sampleRate, int channels, int bitsPerSample)
        {
            this.bitsPerSample = bitsPerSample is 16 or 24 or 32 ? bitsPerSample : 24;
            isFloat = this.bitsPerSample == 32;

            // FileShare.Read lets the user open the WAV in a player mid-recording to check
            // progress. ReadWrite access is required because we seek back to patch the
            // header. 8 KB stream buffer balances responsiveness (small enough that a
            // crash loses at most ~50 ms at 48 kHz / 16-bit stereo) with throughput.
            stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 8192, useAsync: false);

            WriteInitialHeader(sampleRate, channels);
            dataStartPos = stream.Position;
            dataChunkSizeFieldPos = dataStartPos - 4;
            lastHeaderRefreshUtc = DateTime.UtcNow;
        }

        private void WriteInitialHeader(int sampleRate, int channels)
        {
            var formatCode = (ushort)(isFloat ? 3 : 1);
            var byteRate = (uint)(sampleRate * channels * bitsPerSample / 8);
            var blockAlign = (ushort)(channels * bitsPerSample / 8);
            // PCM fmt chunk is 16 bytes; IEEE-float adds a 2-byte cbSize trailer (zero,
            // meaning no extension data) for a total of 18 bytes.
            var fmtChunkSize = (uint)(isFloat ? 18 : 16);

            using var bw = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true);
            bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            bw.Write((uint)36);  // placeholder RIFF size — patched in FlushHeader
            bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));

            bw.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
            bw.Write(fmtChunkSize);
            bw.Write(formatCode);
            bw.Write((ushort)channels);
            bw.Write((uint)sampleRate);
            bw.Write(byteRate);
            bw.Write(blockAlign);
            bw.Write((ushort)bitsPerSample);
            if (isFloat) bw.Write((ushort)0);  // cbSize: no extra extension fields

            bw.Write(System.Text.Encoding.ASCII.GetBytes("data"));
            bw.Write((uint)0);  // placeholder data chunk size — patched in FlushHeader
        }

        public void Write(ReadOnlySpan<float> samples)
        {
            if (samples.IsEmpty) return;
            int bytesAppended;
            switch (bitsPerSample)
            {
                case 32:
                    bytesAppended = samples.Length * sizeof(float);
                    if (scratchBytes.Length < bytesAppended) scratchBytes = new byte[bytesAppended];
                    System.Runtime.InteropServices.MemoryMarshal.AsBytes(samples).CopyTo(scratchBytes);
                    stream.Write(scratchBytes, 0, bytesAppended);
                    break;
                case 24:
                    bytesAppended = samples.Length * 3;
                    if (scratchBytes.Length < bytesAppended) scratchBytes = new byte[bytesAppended];
                    PcmPack.FloatToInt24LE(samples, scratchBytes.AsSpan(0, bytesAppended));
                    stream.Write(scratchBytes, 0, bytesAppended);
                    break;
                default: // 16
                    bytesAppended = samples.Length * 2;
                    if (scratchBytes.Length < bytesAppended) scratchBytes = new byte[bytesAppended];
                    var dst = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(scratchBytes.AsSpan(0, bytesAppended));
                    for (var i = 0; i < samples.Length; i++)
                    {
                        var v = Math.Clamp(samples[i], -1f, 1f);
                        dst[i] = (short)(v * 32767f);
                    }
                    stream.Write(scratchBytes, 0, bytesAppended);
                    break;
            }
            dataBytesWritten += bytesAppended;

            // Periodic header refresh — every HeaderRefreshSeconds. We seek back, patch the
            // two size fields, seek forward to the data tail, and flush all the way to disk.
            // The seek + write is cheap (a few bytes); the flush is the expensive part but
            // it's only every ~5 s. A crash any time after the first refresh leaves a
            // playable WAV containing all audio captured up to that refresh.
            if ((DateTime.UtcNow - lastHeaderRefreshUtc).TotalSeconds >= HeaderRefreshSeconds)
            {
                FlushHeader();
                lastHeaderRefreshUtc = DateTime.UtcNow;
            }
        }

        private void FlushHeader()
        {
            var tailPos = stream.Position;
            stream.Position = 4;
            using (var bw = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true))
            {
                bw.Write((uint)(tailPos - 8));  // RIFF chunk size = total file size - 8
            }
            stream.Position = dataChunkSizeFieldPos;
            using (var bw = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true))
            {
                bw.Write((uint)dataBytesWritten);  // data chunk size
            }
            stream.Position = tailPos;
            // Flush forces the OS to push our user-space buffer to the disk cache; FlushFileBuffers
            // (via Flush(true)) would force the disk cache to platter, but that's expensive enough
            // to skip — a kernel crash that loses the disk cache is rare enough not to plan for.
            stream.Flush();
        }

        public void Dispose()
        {
            try { FlushHeader(); } catch { /* best-effort final header patch */ }
            try { stream.Dispose(); } catch { /* best-effort stream close */ }
        }
    }

    /// <summary>MP3 writer. NAudio.Lame's LameMP3FileWriter takes a 16-bit PCM WaveFormat
    /// input and an int kbps for CBR. MP3 is naturally crash-resilient — every encoded
    /// frame is self-contained and the file-on-disk is always a valid (truncated) MP3
    /// representing everything LAME has emitted so far — but LAME and the OS both buffer
    /// internally, so we Flush every <see cref="FlushIntervalSeconds"/> seconds to bound
    /// the loss-on-crash to a couple of seconds rather than however-much fit in the
    /// kernel file cache.</summary>
    private sealed class Mp3FormatWriter : IFormatWriter
    {
        private const int FlushIntervalSeconds = 5;

        private readonly LameMP3FileWriter writer;
        private byte[] scratchBytes = new byte[4096];
        private readonly int channels;
        private DateTime lastFlushUtc;

        public Mp3FormatWriter(string path, int sampleRate, int channels, int bitrateKbps)
        {
            this.channels = channels;
            var pcmFormat = new WaveFormat(sampleRate, 16, channels);
            // Direct kbps constructor — NAudio.Lame accepts a plain int and configures LAME
            // for CBR at that rate. Clamp to the LAME range (8..320 for MPEG-1 layer 3 at
            // 48 kHz). Values from our dialog are 128/192/256/320 so no clamping fires in
            // practice; the guard is for future-proofing if the UI gains finer steps.
            var clamped = Math.Clamp(bitrateKbps, 8, 320);
            writer = new LameMP3FileWriter(path, pcmFormat, clamped);
            lastFlushUtc = DateTime.UtcNow;
        }

        public void Write(ReadOnlySpan<float> samples)
        {
            if (samples.IsEmpty) return;
            var byteLength = samples.Length * 2;
            if (scratchBytes.Length < byteLength) scratchBytes = new byte[byteLength];
            var dst = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(scratchBytes.AsSpan(0, byteLength));
            for (var i = 0; i < samples.Length; i++)
            {
                var v = Math.Clamp(samples[i], -1f, 1f);
                dst[i] = (short)(v * 32767f);
            }
            writer.Write(scratchBytes, 0, byteLength);

            if ((DateTime.UtcNow - lastFlushUtc).TotalSeconds >= FlushIntervalSeconds)
            {
                try { writer.Flush(); } catch { /* flush is best-effort */ }
                lastFlushUtc = DateTime.UtcNow;
            }
        }

        public void Dispose() => writer.Dispose();
    }

    /// <summary>OGG-Opus writer. Reuses the Concentus encoder that the wire path uses, wrapped
    /// in the Concentus.Oggfile OGG container writer so the result is a standard .opus file
    /// playable in VLC / mpv / browsers.
    ///
    /// Opus operates on fixed-size frames (we use 20 ms = 960 samples per channel at 48 kHz).
    /// The writer buffers incoming float samples, converts to int16, and emits one frame to
    /// the Ogg writer per accumulated chunk. Any partial frame at Dispose is zero-padded and
    /// flushed so no audio is lost.
    ///
    /// Crash resilience: the OGG container is a stream of self-contained packets, so the file
    /// on disk is always a valid (truncated) Opus file representing everything written so far.
    /// We Flush the underlying FileStream every <see cref="FlushIntervalSeconds"/> seconds to
    /// bound loss-on-crash to that window.</summary>
    private sealed class OggOpusFormatWriter : IFormatWriter
    {
        private const int FlushIntervalSeconds = 5;
        private const int OpusFrameSamplesPerChannel = 960; // 20 ms at 48 kHz

        private readonly FileStream fileStream;
        private readonly IOpusEncoder encoder;
        private readonly OpusOggWriteStream writer;
        private readonly int channels;
        private readonly short[] frameScratch;
        private int frameScratchWritten; // interleaved shorts buffered toward the next frame
        private DateTime lastFlushUtc;

        public OggOpusFormatWriter(string path, int sampleRate, int channels, int bitrateKbps)
        {
            this.channels = channels;
            // Frame scratch holds one full Opus frame of interleaved shorts.
            frameScratch = new short[OpusFrameSamplesPerChannel * channels];

            encoder = OpusCodecFactory.CreateEncoder(sampleRate, channels, OpusApplication.OPUS_APPLICATION_AUDIO);
            encoder.Bitrate = Math.Clamp(bitrateKbps, 6, 510) * 1000;
            // VBR mode unconstrained — Opus's default for music. Good music quality at the
            // bitrates we expose (96..256 kbps).
            encoder.UseVBR = true;
            encoder.UseConstrainedVBR = false;

            fileStream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 8192, useAsync: false);
            writer = new OpusOggWriteStream(encoder, fileStream, null, sampleRate);
            lastFlushUtc = DateTime.UtcNow;
        }

        public void Write(ReadOnlySpan<float> samples)
        {
            if (samples.IsEmpty) return;
            // Convert float → int16 inline as we copy into the per-frame scratch. Flush a
            // complete Opus frame to the OGG writer each time the scratch is full.
            for (var i = 0; i < samples.Length; i++)
            {
                var v = samples[i];
                if (v > 1f) v = 1f; else if (v < -1f) v = -1f;
                frameScratch[frameScratchWritten++] = (short)(v * 32767f);
                if (frameScratchWritten >= frameScratch.Length)
                {
                    writer.WriteSamples(frameScratch, 0, frameScratch.Length);
                    frameScratchWritten = 0;
                }
            }

            if ((DateTime.UtcNow - lastFlushUtc).TotalSeconds >= FlushIntervalSeconds)
            {
                try { fileStream.Flush(); } catch { /* flush is best-effort */ }
                lastFlushUtc = DateTime.UtcNow;
            }
        }

        public void Dispose()
        {
            // Final partial frame: pad with zeros so the encoder has a full frame to encode,
            // then call Finish() to write the OGG end-of-stream packet so the file is well-formed.
            try
            {
                if (frameScratchWritten > 0)
                {
                    Array.Clear(frameScratch, frameScratchWritten, frameScratch.Length - frameScratchWritten);
                    writer.WriteSamples(frameScratch, 0, frameScratch.Length);
                    frameScratchWritten = 0;
                }
                writer.Finish();
            }
            catch { /* best-effort final flush */ }
            try { fileStream.Dispose(); } catch { /* best-effort close */ }
            // 2026-05-27 — release native libopus state owned by the recording encoder. The
            // concrete encoder is NativeOpusEncoder under Concentus.Native; not disposing it
            // leaked native memory on every recording stop. See StreamSession.Dispose for the
            // full backstory.
            try { (encoder as IDisposable)?.Dispose(); } catch { /* best-effort */ }
        }
    }

    /// <summary>FLAC writer using CUETools.Codecs.FLAKE — pure-managed FLAC encoder, no
    /// native DLL. Lossless at every compression level; level 5 (default) matches the
    /// libFLAC reference encoder's default speed/size compromise.
    ///
    /// FLAC is integer-PCM only — 16 or 24 bit. Float input is scaled to the configured bit
    /// depth with hard clamping at the rails.
    ///
    /// Crash resilience: FLAC's stream format is self-framing — every frame is independently
    /// decodable. A truncated file remains a valid (shorter) FLAC representing everything
    /// Flake emitted so far. We Flush the underlying stream every <see cref="FlushIntervalSeconds"/>
    /// seconds to bound the OS-cache-loss window.</summary>
    private sealed class FlacFormatWriter : IFormatWriter
    {
        private const int FlushIntervalSeconds = 5;

        private readonly FileStream fileStream;
        private readonly FlakeWriter writer;
        private readonly AudioPCMConfig config;
        private readonly int channels;
        private readonly int bitsPerSample;
        private readonly int bytesPerSample;
        private readonly int scaleFactor;
        // Reused per-Write byte buffer in the packed PCM layout the AudioBuffer constructor
        // accepts. Interleaved [L0 R0 L1 R1 ...], with each sample serialised as
        // signed little-endian using <see cref="bytesPerSample"/> bytes.
        private byte[] packedBytes = new byte[4096];
        private DateTime lastFlushUtc;

        public FlacFormatWriter(string path, int sampleRate, int channels, int bitsPerSample, int compressionLevel)
        {
            this.channels = channels;
            // FLAC accepts 16 or 24 here. Anything else (e.g. WAV's 32-bit-float leaking
            // through) coerces to 24, which matches the wire bit depth.
            this.bitsPerSample = bitsPerSample is 16 or 24 ? bitsPerSample : 24;
            bytesPerSample = this.bitsPerSample / 8;
            scaleFactor = (1 << (this.bitsPerSample - 1)) - 1;

            config = new AudioPCMConfig(this.bitsPerSample, channels, sampleRate);
            fileStream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 8192, useAsync: false);
            writer = new FlakeWriter(path, fileStream, config)
            {
                CompressionLevel = Math.Clamp(compressionLevel, 0, 8),
            };
            lastFlushUtc = DateTime.UtcNow;
        }

        public void Write(ReadOnlySpan<float> samples)
        {
            if (samples.IsEmpty) return;
            var frames = samples.Length / channels;
            if (frames <= 0) return;

            // Pack interleaved float → signed little-endian PCM (2 or 3 bytes per sample).
            var byteLen = samples.Length * bytesPerSample;
            if (packedBytes.Length < byteLen) packedBytes = new byte[byteLen];
            if (bitsPerSample == 16)
            {
                for (var i = 0; i < samples.Length; i++)
                {
                    var v = samples[i];
                    if (v > 1f) v = 1f; else if (v < -1f) v = -1f;
                    var s = (short)(v * 32767f);
                    var off = i * 2;
                    packedBytes[off] = (byte)(s & 0xFF);
                    packedBytes[off + 1] = (byte)((s >> 8) & 0xFF);
                }
            }
            else // 24
            {
                for (var i = 0; i < samples.Length; i++)
                {
                    var v = samples[i];
                    if (v > 1f) v = 1f; else if (v < -1f) v = -1f;
                    var s = (int)(v * 8388607f); // 2^23 - 1
                    var off = i * 3;
                    packedBytes[off] = (byte)(s & 0xFF);
                    packedBytes[off + 1] = (byte)((s >> 8) & 0xFF);
                    packedBytes[off + 2] = (byte)((s >> 16) & 0xFF);
                }
            }

            // AudioBuffer(config, byte[], frameCount) wraps the packed bytes without copying.
            // FlakeWriter encodes one block per Write call; block size adapts to the supplied
            // frame count.
            var buf = new AudioBuffer(config, packedBytes, frames);
            writer.Write(buf);

            if ((DateTime.UtcNow - lastFlushUtc).TotalSeconds >= FlushIntervalSeconds)
            {
                try { fileStream.Flush(); } catch { /* flush is best-effort */ }
                lastFlushUtc = DateTime.UtcNow;
            }
        }

        public void Dispose()
        {
            try { writer.Close(); } catch { /* best-effort final flush */ }
            try { fileStream.Dispose(); } catch { /* best-effort close */ }
        }
    }
}
