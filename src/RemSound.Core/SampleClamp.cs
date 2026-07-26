namespace RemSound.Core;

/// <summary>
/// The encoder-boundary hard clamp, shared by all three capture paths (mix engine, ASIO backend,
/// push-mode WASAPI backend) — which used to carry three private copies of it. Clamps every sample to
/// [−1, +1] and returns how many were clipped, so callers batch ONE Interlocked.Add per buffer instead
/// of per-sample interlocked increments on the real-time path (the ASIO copy did up to four per frame).
/// Exactly ±1 is NOT clipping — only samples beyond the range count.
/// </summary>
public static class SampleClamp
{
    public static long ClampBuffer(Span<float> samples)
    {
        long clipped = 0;
        for (var i = 0; i < samples.Length; i++)
        {
            var v = samples[i];
            if (v > 1f) { samples[i] = 1f; clipped++; }
            else if (v < -1f) { samples[i] = -1f; clipped++; }
        }
        return clipped;
    }
}
