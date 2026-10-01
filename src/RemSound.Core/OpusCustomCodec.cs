using System.Runtime.InteropServices;

namespace RemSound.Core;

/// <summary>
/// Opus custom modes (Jamulus-style Opus frames, 2026-09-26; automatic since 2026-10-01, see JamulusStyle): 64- and 128-sample CELT frames
/// (1.33 and 2.67 ms at 48 kHz), which standard Opus cannot make - its shortest frame is 2.5 ms, and the opus.dll that
/// Concentus.Native ships was built without custom modes. These calls go to remsound-opus.dll, our own libopus 1.6.1
/// build with custom modes, tuned for speed (third-party/remsound-opus/BUILD.md). The app loads it at start-up, on its UI
/// thread; the normal Opus path never touches it, and the service never loads it.
///
/// <para>Custom-mode packets are NOT standard Opus: no standard decoder can play them (an iPhone would even play them as
/// PCM noise). So they only ever go to a copy of RemSound that says in its Format packets that it can decode them - see
/// <see cref="AudioFormatInfo.LabFlags"/> and CustomOpusPeers.</para>
/// </summary>
public static class OpusCustomNative
{
    private const string Dll = "remsound-opus.dll";

    private static readonly object loadGate = new();
    private static bool loadTried;
    private static bool loaded;
    private static string loadMessage = "not tried";

    /// <summary>The frame sizes this build encodes and decodes.</summary>
    public static bool IsSupportedFrameSize(int samples) => samples is 64 or 128 or 256;

    /// <summary>Load remsound-opus.dll from beside the program, once. False (with a reason in <see cref="LoadMessage"/>)
    /// if it is missing or not a custom-modes build; the switch then does nothing and the normal stream carries on.</summary>
    public static bool TryLoad()
    {
        lock (loadGate)
        {
            if (loadTried) return loaded;
            loadTried = true;
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, Dll);
                if (!File.Exists(path)) { loadMessage = $"{Dll} is not beside the program"; return loaded = false; }
                if (!NativeLibrary.TryLoad(path, out var handle)) { loadMessage = $"{Dll} would not load"; return loaded = false; }
                if (!NativeLibrary.TryGetExport(handle, "opus_custom_encoder_create", out _))
                {
                    loadMessage = $"{Dll} was built without custom modes";
                    return loaded = false;
                }
                var version = Marshal.PtrToStringAnsi(opus_get_version_string()) ?? "unknown";
                loadMessage = $"{Dll} loaded ({version}, custom modes)";
                return loaded = true;
            }
            catch (Exception ex)
            {
                loadMessage = $"{Dll}: {ex.GetType().Name}: {ex.Message}";
                return loaded = false;
            }
        }
    }

    /// <summary>What happened when the library was loaded, for the log.</summary>
    public static string LoadMessage { get { lock (loadGate) return loadMessage; } }

    /// <summary>True once the library has loaded (without trying to load it).</summary>
    public static bool Available { get { lock (loadGate) return loaded; } }

    // libopus request numbers (opus_defines.h).
    internal const int OPUS_SET_BITRATE_REQUEST = 4002;
    internal const int OPUS_SET_VBR_REQUEST = 4006;
    internal const int OPUS_SET_COMPLEXITY_REQUEST = 4010;
    internal const int OPUS_SET_PACKET_LOSS_PERC_REQUEST = 4014;

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr opus_get_version_string();
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr opus_custom_mode_create(int fs, int frameSize, out int error);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void opus_custom_mode_destroy(IntPtr mode);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr opus_custom_encoder_create(IntPtr mode, int channels, out int error);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void opus_custom_encoder_destroy(IntPtr st);
    // opus_custom_encoder_ctl is variadic; on x64 Windows a variadic int argument is passed exactly like a fixed one.
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int opus_custom_encoder_ctl(IntPtr st, int request, int value);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern unsafe int opus_custom_encode_float(IntPtr st, float* pcm, int frameSize, byte* compressed, int maxCompressedBytes);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr opus_custom_decoder_create(IntPtr mode, int channels, out int error);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void opus_custom_decoder_destroy(IntPtr st);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern unsafe int opus_custom_decode_float(IntPtr st, byte* data, int len, float* pcm, int frameSize);
}

/// <summary>One custom-mode Opus encoder: fixed frame size, stereo, constant bitrate (every packet the same size, as
/// Jamulus does). Not thread-safe: one audio thread owns it.</summary>
public sealed class OpusCustomEncoder : IDisposable
{
    private IntPtr mode;
    private IntPtr encoder;

    public int FrameSamplesPerChannel { get; }
    public int Channels { get; }
    public int Bitrate { get; }

    /// <summary>Bytes per packet at this bitrate and frame size (constant bitrate).</summary>
    public int BytesPerFrame => Math.Clamp(Bitrate * FrameSamplesPerChannel / (8 * 48000), 16, 1275);

    /// <summary>The bitrate used for a frame size: tiny frames carry more overhead per second, so they get more bits.</summary>
    public static int BitrateFor(int frameSamples) => frameSamples <= 64 ? 320_000 : 256_000;

    public OpusCustomEncoder(int frameSamplesPerChannel, int channels = 2, int bitrate = 0)
    {
        if (!OpusCustomNative.IsSupportedFrameSize(frameSamplesPerChannel)) throw new ArgumentOutOfRangeException(nameof(frameSamplesPerChannel));
        if (!OpusCustomNative.TryLoad()) throw new InvalidOperationException(OpusCustomNative.LoadMessage);
        FrameSamplesPerChannel = frameSamplesPerChannel;
        Channels = channels;
        Bitrate = bitrate > 0 ? bitrate : BitrateFor(frameSamplesPerChannel);
        mode = OpusCustomNative.opus_custom_mode_create(48000, frameSamplesPerChannel, out var err);
        if (mode == IntPtr.Zero || err != 0) throw new InvalidOperationException($"opus_custom_mode_create({frameSamplesPerChannel}) failed: {err}");
        encoder = OpusCustomNative.opus_custom_encoder_create(mode, channels, out err);
        if (encoder == IntPtr.Zero || err != 0)
        {
            OpusCustomNative.opus_custom_mode_destroy(mode);
            mode = IntPtr.Zero;
            throw new InvalidOperationException($"opus_custom_encoder_create failed: {err}");
        }
        // Constant bitrate, like Jamulus: fixed-size packets. Full complexity: the latency is the same at any complexity,
        // and a desktop encodes a 64-sample CELT frame in microseconds. A packet-loss figure of 35 % switches CELT's pitch
        // pre-filter off (Jamulus does the same), which makes a concealed frame sound cleaner.
        OpusCustomNative.opus_custom_encoder_ctl(encoder, OpusCustomNative.OPUS_SET_VBR_REQUEST, 0);
        OpusCustomNative.opus_custom_encoder_ctl(encoder, OpusCustomNative.OPUS_SET_BITRATE_REQUEST, Bitrate);
        OpusCustomNative.opus_custom_encoder_ctl(encoder, OpusCustomNative.OPUS_SET_COMPLEXITY_REQUEST, 10);
        OpusCustomNative.opus_custom_encoder_ctl(encoder, OpusCustomNative.OPUS_SET_PACKET_LOSS_PERC_REQUEST, 35);
    }

    /// <summary>Encode exactly one frame of interleaved float samples. Returns the packet length, or a negative libopus
    /// error.</summary>
    public unsafe int Encode(ReadOnlySpan<float> interleaved, Span<byte> packet)
    {
        if (encoder == IntPtr.Zero) throw new ObjectDisposedException(nameof(OpusCustomEncoder));
        if (interleaved.Length < FrameSamplesPerChannel * Channels) throw new ArgumentException("short frame", nameof(interleaved));
        fixed (float* pcm = interleaved)
        fixed (byte* outp = packet)
        {
            return OpusCustomNative.opus_custom_encode_float(encoder, pcm, FrameSamplesPerChannel, outp, Math.Min(packet.Length, BytesPerFrame));
        }
    }

    public void Dispose()
    {
        if (encoder != IntPtr.Zero) { OpusCustomNative.opus_custom_encoder_destroy(encoder); encoder = IntPtr.Zero; }
        if (mode != IntPtr.Zero) { OpusCustomNative.opus_custom_mode_destroy(mode); mode = IntPtr.Zero; }
    }
}

/// <summary>One custom-mode Opus decoder, with packet-loss concealment (decode with no packet, as Jamulus does for every
/// missing frame). Not thread-safe: one thread owns it.</summary>
public sealed class OpusCustomDecoder : IDisposable
{
    private IntPtr mode;
    private IntPtr decoder;

    public int FrameSamplesPerChannel { get; }
    public int Channels { get; }

    public OpusCustomDecoder(int frameSamplesPerChannel, int channels = 2)
    {
        if (!OpusCustomNative.IsSupportedFrameSize(frameSamplesPerChannel)) throw new ArgumentOutOfRangeException(nameof(frameSamplesPerChannel));
        if (!OpusCustomNative.TryLoad()) throw new InvalidOperationException(OpusCustomNative.LoadMessage);
        FrameSamplesPerChannel = frameSamplesPerChannel;
        Channels = channels;
        mode = OpusCustomNative.opus_custom_mode_create(48000, frameSamplesPerChannel, out var err);
        if (mode == IntPtr.Zero || err != 0) throw new InvalidOperationException($"opus_custom_mode_create({frameSamplesPerChannel}) failed: {err}");
        decoder = OpusCustomNative.opus_custom_decoder_create(mode, channels, out err);
        if (decoder == IntPtr.Zero || err != 0)
        {
            OpusCustomNative.opus_custom_mode_destroy(mode);
            mode = IntPtr.Zero;
            throw new InvalidOperationException($"opus_custom_decoder_create failed: {err}");
        }
    }

    /// <summary>Decode one packet into interleaved floats. Returns samples per channel, or a negative libopus error.</summary>
    public unsafe int Decode(ReadOnlySpan<byte> packet, Span<float> interleaved)
    {
        if (decoder == IntPtr.Zero) throw new ObjectDisposedException(nameof(OpusCustomDecoder));
        if (interleaved.Length < FrameSamplesPerChannel * Channels) throw new ArgumentException("short output", nameof(interleaved));
        fixed (byte* data = packet)
        fixed (float* pcm = interleaved)
        {
            return OpusCustomNative.opus_custom_decode_float(decoder, data, packet.Length, pcm, FrameSamplesPerChannel);
        }
    }

    /// <summary>Make up one missing frame from what came before (Opus packet-loss concealment).</summary>
    public unsafe int Conceal(Span<float> interleaved)
    {
        if (decoder == IntPtr.Zero) throw new ObjectDisposedException(nameof(OpusCustomDecoder));
        if (interleaved.Length < FrameSamplesPerChannel * Channels) throw new ArgumentException("short output", nameof(interleaved));
        fixed (float* pcm = interleaved)
        {
            return OpusCustomNative.opus_custom_decode_float(decoder, null, 0, pcm, FrameSamplesPerChannel);
        }
    }

    public void Dispose()
    {
        if (decoder != IntPtr.Zero) { OpusCustomNative.opus_custom_decoder_destroy(decoder); decoder = IntPtr.Zero; }
        if (mode != IntPtr.Zero) { OpusCustomNative.opus_custom_mode_destroy(mode); mode = IntPtr.Zero; }
    }
}

/// <summary>
/// The copies of RemSound that have said they can decode custom-mode Opus: their Format packets carry
/// <see cref="AudioFormatInfo.LabFlagCanDecodeCustomOpus"/>, which a RemSound sets only while it plays through ASIO. Keyed
/// by address, forgotten a few seconds after the last such packet (Format packets repeat every 250 ms), so switching it
/// off at the far end brings the normal stream back within seconds.
/// </summary>
public static class CustomOpusPeers
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<System.Net.IPAddress, long> lastSeen = new();
    private static readonly long ForgetAfterTicks = System.Diagnostics.Stopwatch.Frequency * 3;

    /// <summary>A Format packet from <paramref name="address"/> carried the flag.</summary>
    public static void Note(System.Net.IPAddress address) => lastSeen[address] = System.Diagnostics.Stopwatch.GetTimestamp();

    /// <summary>Whether <paramref name="address"/> has said, in the last few seconds, that it can decode custom Opus.</summary>
    public static bool CanDecode(System.Net.IPAddress address) =>
        lastSeen.TryGetValue(address, out var at) && System.Diagnostics.Stopwatch.GetTimestamp() - at < ForgetAfterTicks;

    /// <summary>How many addresses currently qualify (for the log).</summary>
    public static int Count
    {
        get
        {
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            return lastSeen.Count(kv => now - kv.Value < ForgetAfterTicks);
        }
    }

    /// <summary>Forget everyone (tests).</summary>
    public static void Clear() => lastSeen.Clear();

    /// <summary>Gate seam (the soak and stress steps): every address remembered, recent or not - the table's real size,
    /// where <see cref="Count"/> counts only the recent ones.</summary>
    internal static int EntriesForTest => lastSeen.Count;
}
