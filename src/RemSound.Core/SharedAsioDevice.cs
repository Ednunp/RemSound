using System.Diagnostics;
using NAudio.Wave;
using NAudio.Wave.Asio;

namespace RemSound.Core;

/// <summary>
/// ONE driver instance per ASIO driver, shared by capture and playback.
///
/// <para><b>Why.</b> The capture backend and the render backend each used to create their own
/// <see cref="AsioOut"/> for the same driver name — two COM instances of one driver in one process.
/// Ed's Audient, Komplete and Focusrite drivers allow that. The Zoom H4essential does not: the second
/// instance fails with NAudio's "Unable to instantiate ASIO. Check if STAThread is set", which is what
/// NAudio says whenever the driver's <c>CoCreateInstance</c> fails, whatever the reason. So on the Zoom
/// you could play RemSound or send from its inputs, never both (Anthony Reyers, 2026-09-04). It is also
/// not how ASIO is meant to be used: a driver is full duplex by design, one instance, one callback,
/// inputs and outputs handed over together.</para>
///
/// <para><b>What.</b> A registry keyed by driver name. Whoever needs the driver acquires it; the first
/// side to attach opens it ONCE, full duplex, with the driver's whole input and output channel counts —
/// the "never reopen" rule both backends already lived by, for the same single-client drivers — and it
/// stays open until the last holder lets go. The capture side attaches an <c>AudioAvailable</c> handler
/// and reads the inputs; the playback side attaches a wave provider, which this device wraps so the
/// driver hears silence until then. NAudio's callback already supports exactly this split: the handler
/// runs first, and if it did not write the outputs the provider is read for them.</para>
///
/// <para><b>What it also buys.</b> Capture and playback on one interface now share one callback and one
/// clock, so the send and receive paths on that interface cannot drift against each other. And a
/// driver that only exists as a bridge — ReaRoute — works in both directions on one instance.</para>
///
/// <para><b>Threading.</b> Every control call — create, init, play, stop, dispose — runs on this device's
/// own <see cref="AsioApartment"/>, the single pumped STA thread that stopped the native crash on close
/// (see that type). The audio callback runs on the driver's real-time thread and only ever reads a
/// volatile delegate. Opening holds the device's gate for the duration of the open, so a second side
/// attaching during a slow open simply waits for it.</para>
///
/// <para><b>Lives in Core</b> because both the sender and the receiver need it and neither may
/// reference the other. It is the one type in Core that touches NAudio; <see cref="AsioApartment"/>
/// was already here for the same reason.</para>
/// </summary>
public sealed class SharedAsioDevice
{
    public const int SampleRate = 48000;

    private static readonly object Registry = new();
    private static readonly Dictionary<string, SharedAsioDevice> Devices = new(StringComparer.OrdinalIgnoreCase);

    private readonly object gate = new();
    private readonly object sinkGate = new();
    private readonly List<Action<string>> sinks = [];
    private readonly OutputTap output = new();
    private AsioApartment? apartment;
    private AsioOut? asio;
    private int holders;
    private volatile EventHandler<AsioAudioAvailableEventArgs>? captureHandler;

    public string DriverName { get; }
    /// <summary>The driver's own channel counts, valid once open.</summary>
    public int InputChannelCount { get; private set; }
    public int OutputChannelCount { get; private set; }
    /// <summary>The driver's buffer size in frames, valid once open. Audio accumulates for exactly one
    /// buffer before the callback fires, so this IS the capture wait.</summary>
    public int FramesPerBuffer { get; private set; }
    /// <summary>The driver's own figure for how long playback takes, in samples, valid once open.</summary>
    public int PlaybackLatencySamples { get; private set; }

    public bool IsOpen { get { lock (gate) return asio is not null; } }

    private SharedAsioDevice(string driverName) => DriverName = driverName;

    /// <summary>Take a hold on the driver. Nothing is opened until a side attaches. Every Acquire must
    /// be paired with a <see cref="Release"/>; the last one closes the driver.</summary>
    public static SharedAsioDevice Acquire(string driverName, Action<string>? log)
    {
        SharedAsioDevice device;
        lock (Registry)
        {
            if (!Devices.TryGetValue(driverName, out device!))
            {
                device = new SharedAsioDevice(driverName);
                Devices[driverName] = device;
            }
            device.holders++;
        }
        if (log is not null) lock (device.sinkGate) device.sinks.Add(log);
        return device;
    }

    /// <summary>Is RemSound holding this driver open right now?</summary>
    public static bool IsOpenFor(string driverName)
    {
        SharedAsioDevice? device;
        lock (Registry) Devices.TryGetValue(driverName, out device);
        return device?.IsOpen == true;
    }

    /// <summary>Channel counts and names from the instance RemSound already holds open, so nobody has
    /// to open a second one to ask — which fails on a driver that allows one per process, and would
    /// have emptied the channel lists for exactly the driver in use. False when the driver is not
    /// open here, in which case a caller may open its own short-lived instance as before.</summary>
    public static bool TryDescribe(string driverName, out int inputs, out int outputs, out string[] inputNames, out string[] outputNames)
    {
        inputs = outputs = 0;
        inputNames = outputNames = [];
        SharedAsioDevice? device;
        lock (Registry) Devices.TryGetValue(driverName, out device);
        if (device is null) return false;
        AsioOut? driver;
        AsioApartment? home;
        lock (device.gate) { driver = device.asio; home = device.apartment; }
        if (driver is null || home is null) return false;
        try
        {
            var ins = new List<string>();
            var outs = new List<string>();
            var ok = home.Invoke(() =>
            {
                for (var i = 0; i < driver.DriverInputChannelCount; i++)
                {
                    try { ins.Add(driver.AsioInputChannelName(i)); } catch { ins.Add($"Input {i + 1}"); }
                }
                for (var i = 0; i < driver.DriverOutputChannelCount; i++)
                {
                    try { outs.Add(driver.AsioOutputChannelName(i)); } catch { outs.Add($"Output {i + 1}"); }
                }
            }, timeoutMs: 5_000);
            if (!ok) return false;
            inputs = ins.Count;
            outputs = outs.Count;
            inputNames = [.. ins];
            outputNames = [.. outs];
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>What a failed open most likely means, in words the status line can show. NAudio's own
    /// message for a driver that refuses to be created is "Unable to instantiate ASIO. Check if
    /// STAThread is set", which points at a threading mistake that is not the cause.</summary>
    public static string Explain(Exception ex)
    {
        var message = ex.Message;
        if (message.Contains("instantiate ASIO", StringComparison.OrdinalIgnoreCase))
        {
            return message + " - the driver would not open. Another program may be holding it, and some drivers allow "
                 + "one connection at a time. RemSound itself opens each driver once, for playback and capture together";
        }
        return message;
    }

    /// <summary>Open the driver if it is not open yet: full duplex, all channels, at 48 kHz. Throws if
    /// the driver will not open, leaving the device unopened so a later attach can try again.</summary>
    public void EnsureOpen()
    {
        lock (gate)
        {
            if (asio is not null) return;
            apartment ??= new AsioApartment($"asio:{DriverName}", Log);
            apartment.Invoke(() =>
            {
                Log($"asio open: creating driver \"{DriverName}\"");
                var driver = new AsioOut(DriverName);
                try
                {
                    var inputs = driver.DriverInputChannelCount;
                    var outputs = driver.DriverOutputChannelCount;
                    if (inputs <= 0 && outputs <= 0)
                        throw new InvalidOperationException($"driver \"{DriverName}\" reports no input or output channels");
                    driver.ChannelOffset = 0;
                    driver.InputChannelOffset = 0;
                    output.Configure(outputs);
                    // Both directions in ONE init, on the whole channel count each way. Playback gets
                    // silence until a side attaches; the inputs are read and discarded until a side
                    // attaches. Either count may be zero (a bridge with outputs only, a microphone with
                    // inputs only), and NAudio handles a null provider or zero record channels.
                    Log($"asio open: init full duplex ({inputs} in, {outputs} out @ {SampleRate} Hz)");
                    driver.InitRecordAndPlayback(outputs > 0 ? output : null, inputs, SampleRate);
                    if (inputs > 0)
                    {
                        driver.AudioAvailable += OnAudioAvailable;
                    }
                    Log("asio open: starting stream (play)");
                    driver.Play();
                    InputChannelCount = inputs;
                    OutputChannelCount = outputs;
                    FramesPerBuffer = driver.FramesPerBuffer;
                    PlaybackLatencySamples = driver.PlaybackLatency;
                    Log($"asio open: running \"{DriverName}\" {SampleRate} Hz, {inputs} in, {outputs} out, {FramesPerBuffer} frames per buffer"
                        + (PlaybackLatencySamples > 0 ? $", driver reports {PlaybackLatencySamples} samples of playback latency" : ""));
                    asio = driver;
                }
                catch
                {
                    try { driver.Dispose(); } catch { /* the failure that matters is the one in flight */ }
                    throw;
                }
            });
        }
    }

    /// <summary>Capture attaches here. The handler runs on the driver's real-time thread with the
    /// callback's input buffers; it must not write the outputs.</summary>
    public void AttachCapture(EventHandler<AsioAudioAvailableEventArgs> handler)
    {
        EnsureOpen();
        captureHandler = handler;
    }

    /// <summary>Stop delivering input. A volatile write: the very next callback sees it. The driver
    /// keeps running for whoever else holds it.</summary>
    public void DetachCapture() => captureHandler = null;

    /// <summary>Playback attaches here. The provider must produce IEEE float at
    /// <see cref="SampleRate"/> with <see cref="OutputChannelCount"/> channels — build it after
    /// <see cref="EnsureOpen"/>, which is where that count comes from.</summary>
    public void AttachPlayback(IWaveProvider provider)
    {
        EnsureOpen();
        if (provider.WaveFormat.Channels != OutputChannelCount)
            throw new ArgumentException($"playback provider has {provider.WaveFormat.Channels} channels, the driver has {OutputChannelCount}");
        output.Attach(provider);
    }

    /// <summary>Back to silence on the outputs. The driver keeps running for whoever else holds it.</summary>
    public void DetachPlayback() => output.Attach(null);

    /// <summary>Let go. The last holder closes the driver, on the apartment thread, bounded by
    /// <paramref name="closeTimeoutMs"/> exactly as the backends always were.</summary>
    public void Release(Action<string>? log, int closeTimeoutMs)
    {
        if (log is not null) lock (sinkGate) sinks.Remove(log);
        bool last;
        lock (Registry)
        {
            holders--;
            last = holders <= 0;
            if (last) Devices.Remove(DriverName);
        }
        if (last) Close(closeTimeoutMs);
    }

    private void Close(int closeTimeoutMs)
    {
        AsioOut? toClose;
        AsioApartment? home;
        lock (gate)
        {
            toClose = asio;
            asio = null;
            home = apartment;
            apartment = null;
            captureHandler = null;
            output.Attach(null);
        }
        if (toClose is not null && home is not null)
        {
            // The same shape the backends used, because it is the shape that stopped the crash: unhook,
            // let an in-flight callback return, stop, dispose — all on the apartment thread, bounded, with
            // the elapsed time logged so slow drivers accumulate real figures rather than guesses.
            var closeStart = Stopwatch.GetTimestamp();
            var closed = home.Invoke(() =>
            {
                Log("asio close: unhooking callback");
                try { toClose.AudioAvailable -= OnAudioAvailable; } catch { /* ignore */ }
                Thread.Sleep(60);
                Log("asio close: stopping stream");
                var stopStart = Stopwatch.GetTimestamp();
                try { toClose.Stop(); } catch (Exception ex) { Log($"asio close: stop threw {ex.GetType().Name}: {ex.Message}"); }
                var stopMs = (Stopwatch.GetTimestamp() - stopStart) * 1000 / Stopwatch.Frequency;
                Log($"asio close: releasing driver (dispose) - stop took {stopMs} ms");
                try { toClose.Dispose(); } catch (Exception ex) { Log($"asio close: dispose threw {ex.GetType().Name}: {ex.Message}"); }
            }, timeoutMs: closeTimeoutMs);
            var elapsedMs = (Stopwatch.GetTimestamp() - closeStart) * 1000 / Stopwatch.Frequency;
            Log(closed
                ? $"asio close: complete in {elapsedMs} ms"
                : $"asio close: gave up waiting after {elapsedMs} ms (bound {closeTimeoutMs} ms) - the close is STILL RUNNING on the "
                  + "apartment thread and may yet finish; the card may be briefly unavailable to a re-open");
        }
        home?.Dispose();
        InputChannelCount = 0;
        OutputChannelCount = 0;
        FramesPerBuffer = 0;
        PlaybackLatencySamples = 0;
    }

    private void OnAudioAvailable(object? sender, AsioAudioAvailableEventArgs e)
    {
        var handler = captureHandler;
        if (handler is null) return;
        handler(sender, e);
    }

    /// <summary>One log line to the first holder's sink. The two holders are the sender and the
    /// receiver, each with its own prefix; a line for the shared driver belongs under one of them,
    /// not both.</summary>
    private void Log(string message)
    {
        Action<string>? sink;
        lock (sinkGate) sink = sinks.Count > 0 ? sinks[0] : null;
        sink?.Invoke(message);
    }

    /// <summary>What the driver reads for its outputs: the attached playback provider, or silence.</summary>
    private sealed class OutputTap : IWaveProvider
    {
        private volatile IWaveProvider? attached;

        public WaveFormat WaveFormat { get; private set; } = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 2);

        public void Configure(int channels) => WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Math.Max(1, channels));

        public void Attach(IWaveProvider? provider) => attached = provider;

        public int Read(byte[] buffer, int offset, int count)
        {
            var provider = attached;
            if (provider is null)
            {
                Array.Clear(buffer, offset, count);
                return count;
            }
            var read = provider.Read(buffer, offset, count);
            if (read < count) Array.Clear(buffer, offset + read, count - read);
            return count;
        }
    }
}
