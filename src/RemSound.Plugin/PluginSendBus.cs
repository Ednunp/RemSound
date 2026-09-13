using RemSound.Core;

namespace RemSound.Plugin;

/// <summary>
/// Sums every sending plugin instance in ONE DAW into one block, inside the DAW's own process,
/// before anything crosses the bridge.
///
/// <para><b>Why here and not in the app.</b> Instances in one DAW share a process and are called for
/// the same block boundaries, on the same sample positions. Adding them here is therefore exact
/// addition on already-aligned data: no ring, no rate matching, and — the part that matters — no
/// relative timing offset between two tracks. Summing them in the app instead would mean two block
/// streams arriving over a socket on their own cadences, and the only way to combine those is a
/// buffer per track, which puts one track a variable few milliseconds behind the other. A drum track
/// and a bass track that do not line up is not a latency figure, it is a wrong mix.</para>
///
/// <para><b>The app sees one stream per DAW.</b> Only the leader's link carries the summed block, so
/// however many tracks a session has, RemSound sees a single sending host. That is what keeps the
/// app's side simple enough to be right: <c>PluginTrackSource</c> deals with DAWs, not tracks.</para>
///
/// <para><b>The lock.</b> Hosts are allowed to run their FX graph on several worker threads at once
/// (Reaper's anticipative processing does), so two instances genuinely can be inside
/// <see cref="Submit"/> at the same moment and the accumulator has to be guarded. It is held for one
/// pass over a block of floats — a fraction of a microsecond, uncontended in the ordinary case — and
/// never across a socket call to somebody else's thread. Every alternative that avoids it (a
/// per-instance ring drained by a timer, an interlocked fixed-point accumulator) costs either
/// alignment or precision, which are the two things this class exists to keep.</para>
///
/// <para><b>A stalled instance costs one block of its own audio and nothing else.</b> An instance
/// that stops contributing — bypassed mid-block, its track removed, the host skipping it — would
/// otherwise hold the round open forever. So a second contribution from an instance that has already
/// contributed is taken as proof that a new block has started: the partial round is flushed first,
/// then the new contribution opens the next one.</para>
/// </summary>
internal static class PluginSendBus
{
    private const int Channels = PluginBridgeProtocol.WireChannels;
    private const int MaxFloats = PluginBridgeProtocol.MaxAudioBytes / sizeof(float);

    private static readonly object Gate = new();
    private static readonly Dictionary<Guid, PluginBridgeClient> Senders = new();
    private static readonly HashSet<Guid> Contributed = [];
    private static readonly float[] Accumulator = new float[MaxFloats];
    private static int accumulatedFloats;
    private static Guid leader;

    private static long blocksFlushed;
    private static long partialFlushes;

    /// <summary>Summed blocks handed to the link. For the plugin's own log, and for the gate.</summary>
    internal static long BlocksFlushed => Interlocked.Read(ref blocksFlushed);

    /// <summary>Rounds that were closed by a repeat contribution rather than by everybody arriving —
    /// an instance stopped feeding us. A few around a bypass are normal; a number that climbs is the
    /// finding.</summary>
    internal static long PartialFlushes => Interlocked.Read(ref partialFlushes);

    internal static int SendingInstanceCount { get { lock (Gate) return Senders.Count; } }

    /// <summary>This instance is now sending. Its link becomes the one the summed block goes out on
    /// if it is the first to register.</summary>
    internal static void Register(Guid id, PluginBridgeClient client)
    {
        lock (Gate)
        {
            Senders[id] = client;
            if (leader == Guid.Empty || !Senders.ContainsKey(leader)) leader = id;
        }
    }

    /// <summary>This instance has stopped sending — switched to receiving, deactivated by the host,
    /// or unloaded. Anything it had already contributed to the open round stays in it; dropping the
    /// round instead would put a hole in the other tracks.</summary>
    internal static void Unregister(Guid id)
    {
        lock (Gate)
        {
            if (!Senders.Remove(id)) return;
            Contributed.Remove(id);
            if (leader == id)
            {
                leader = Guid.Empty;
                foreach (var other in Senders.Keys) { leader = other; break; }
                // Nothing left to send on: whatever is half-accumulated has no link to leave by.
                if (leader == Guid.Empty) { accumulatedFloats = 0; Contributed.Clear(); }
            }
            // The round may now be complete without this instance in it.
            if (Senders.Count > 0 && Contributed.Count >= Senders.Count) FlushLocked(partial: false);
        }
    }

    /// <summary>
    /// One instance's block for the current DAW callback. Called on the host's audio thread.
    /// </summary>
    internal static void Submit(Guid id, ReadOnlySpan<float> block)
    {
        if (block.Length == 0 || block.Length > MaxFloats) return;
        lock (Gate)
        {
            if (!Senders.ContainsKey(id)) return;

            // One sending instance — the common case by a wide margin. Straight out, no accumulator
            // touched at all, so the ordinary session pays nothing for a feature it is not using.
            if (Senders.Count == 1 && Contributed.Count == 0)
            {
                if (Senders.TryGetValue(id, out var only) && only.SendTrackBlock(block))
                    Interlocked.Increment(ref blocksFlushed);
                return;
            }

            // Contributing twice means the host has moved on to the next block while somebody who
            // contributed to the last one did not turn up. Close the old round before opening a new.
            if (Contributed.Contains(id)) FlushLocked(partial: true);

            if (accumulatedFloats == 0)
            {
                block.CopyTo(Accumulator);
                accumulatedFloats = block.Length;
            }
            else
            {
                // Tracks in one DAW share a block size, so this is normally an exact match. If a host
                // ever hands two different lengths in one callback, sum what overlaps rather than
                // truncating the round: the shorter track contributes what it has.
                var overlap = Math.Min(accumulatedFloats, block.Length);
                for (var i = 0; i < overlap; i++) Accumulator[i] += block[i];
                if (block.Length > accumulatedFloats)
                {
                    block[accumulatedFloats..].CopyTo(Accumulator.AsSpan(accumulatedFloats));
                    accumulatedFloats = block.Length;
                }
            }
            Contributed.Add(id);
            if (Contributed.Count >= Senders.Count) FlushLocked(partial: false);
        }
    }

    private static void FlushLocked(bool partial)
    {
        var floats = accumulatedFloats;
        accumulatedFloats = 0;
        Contributed.Clear();
        if (floats == 0) return;
        // Whole frames only — a half frame would swap the channels for everything after it.
        floats -= floats % Channels;
        if (floats == 0) return;
        if (!Senders.TryGetValue(leader, out var client)) return;
        if (!client.SendTrackBlock(Accumulator.AsSpan(0, floats))) return;
        Interlocked.Increment(ref blocksFlushed);
        if (partial) Interlocked.Increment(ref partialFlushes);
    }

    /// <summary>Gate seam: put the bus back to a known state between test steps. Never called by the
    /// plugin itself — instances arrive and leave through Register and Unregister.</summary>
    internal static void ResetForTest()
    {
        lock (Gate)
        {
            Senders.Clear();
            Contributed.Clear();
            accumulatedFloats = 0;
            leader = Guid.Empty;
            Interlocked.Exchange(ref blocksFlushed, 0);
            Interlocked.Exchange(ref partialFlushes, 0);
        }
    }
}
