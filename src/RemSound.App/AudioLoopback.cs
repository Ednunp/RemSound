using System.Net;
using RemSound.Core;
using RemSound.Receiver;
using RemSound.Sender;

namespace RemSound.App;

/// <summary>
/// A localhost audio loopback used by the self-test, the diagnostics report and the perf check:
/// capture the default output (as loopback) → encode → send to 127.0.0.1 → receive → decode, for a
/// few seconds, with the receiver rendering to nothing (so no sound is ever produced). Exposes the
/// runtime counters afterwards. Shared so all three callers exercise the identical real audio path.
/// </summary>
internal static class AudioLoopback
{
    /// <summary>A dedicated test port, separate from the live DefaultPort (47830), so a loopback
    /// never clashes with a RemSound instance the user already has running.</summary>
    internal const int TestPort = 47929;

    internal sealed record Result(
        bool Ran, string Codec,
        long PacketsSent, long PacketsReceived, long BytesReceived,
        long Underruns, long Drops, int BufferMs, int TargetLatencyMs,
        string? SkipReason)
    {
        public bool Flowed => Ran && PacketsSent > 0 && PacketsReceived > 0;
    }

    private static Result Skipped(string codec, string why) =>
        new(false, codec, 0, 0, 0, 0, 0, 0, 0, why);

    /// <summary>Run the loopback for <paramref name="seconds"/> and return the counters. Never
    /// throws for an environment reason (no output device, port busy) - it returns a Result with
    /// <see cref="Result.Ran"/> = false and a <see cref="Result.SkipReason"/> instead, so callers
    /// can treat "no audio hardware" as a skip rather than a failure.</summary>
    internal static Result Run(bool opus, int seconds, int port = TestPort)
    {
        var codec = opus ? "Opus" : "PCM";

        IReadOnlyList<AudioDeviceChoice> outputs;
        try { outputs = AudioDeviceCatalog.LoadOutputs(); }
        catch (Exception ex) { return Skipped(codec, "could not enumerate outputs: " + ex.Message); }
        var dev = outputs.FirstOrDefault(o => o.DeviceId is not null);
        if (dev?.DeviceId is not { } deviceId) return Skipped(codec, "no usable output device to capture from");

        using var receiver = new AudioReceiver();
        using var sender = new AudioSender();
        try
        {
            try { receiver.Start(port); }
            catch (Exception ex) { return Skipped(codec, $"could not bind test port {port}: {ex.Message}"); }
            receiver.SetOutputDevices(Array.Empty<string>()); // decode only - never make sound
            sender.ConfigureCodec(opus ? AudioTransportCodec.Opus : AudioTransportCodec.Pcm);
            sender.Configure(new[] { new CaptureSourceSpec(deviceId, CaptureKind.Loopback, dev.Name) });
            sender.SetReceivers(new[] { new IPEndPoint(IPAddress.Loopback, port) });
            sender.Start();
            Thread.Sleep(Math.Max(1, seconds) * 1000);

            // Read the counters while still running (before the finally stops the engines).
            return new Result(
                Ran: true, Codec: codec,
                PacketsSent: sender.PacketsSent,
                PacketsReceived: receiver.PacketsReceived,
                BytesReceived: receiver.BytesReceived,
                Underruns: receiver.Underruns,
                Drops: receiver.Drops,
                BufferMs: receiver.CurrentBufferMs,
                TargetLatencyMs: receiver.TargetLatencyMs,
                SkipReason: null);
        }
        finally
        {
            try { sender.Stop(); } catch { /* ignore */ }
            try { receiver.Stop(); } catch { /* ignore */ }
        }
    }
}
