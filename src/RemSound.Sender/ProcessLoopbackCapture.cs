using System.Runtime.InteropServices;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

// Lets the in-app self-tests (assembly "RemSound") flip ForceSupportedForTest to exercise the Win7 paths.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("RemSound")]

namespace RemSound.Sender;

/// <summary>
/// Captures the audio rendered by ONE process (and its child-process tree) using the Windows
/// process-loopback API — <c>ActivateAudioInterfaceAsync</c> against the virtual device
/// <c>VAD\Process_Loopback</c> with <c>AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK</c>. This is what
/// makes "send only specific applications" possible: unlike ordinary WASAPI loopback (which captures a
/// whole output device), this isolates a single app's render stream.
///
/// <para>Hand-rolled COM interop because NAudio has no binding for the process-loopback activation path.
/// Requires Windows 10 build 19041 (20H1) or newer; on older systems the app-send feature is hidden in
/// the UI so this type is never constructed. Presents as a standard <see cref="IWaveIn"/> so it drops
/// straight into <see cref="CaptureSource"/> alongside the existing WASAPI/ASIO backends.</para>
///
/// <para>The mix format for a process-loopback client is not discoverable (<c>GetMixFormat</c> returns
/// E_NOTIMPL for this virtual device), so we request a fixed 48 kHz / 32-bit-float / stereo shared-mode
/// format — the same format the rest of the pipeline already runs at — and Windows resamples the app's
/// audio to it for us.</para>
/// </summary>
public sealed class ProcessLoopbackCapture : IWaveIn
{
    private const string VirtualDevicePath = "VAD\\Process_Loopback";

    // Fixed capture format — see class remarks. 48 kHz, IEEE float, stereo.
    private static readonly WaveFormat CaptureFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

    private const int AUDCLNT_SHAREMODE_SHARED = 0;
    private const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
    private const uint AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;
    private const uint AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM = 0x80000000;
    private const long BufferDurationHns = 20 * 10_000; // 20 ms in 100-ns units

    private readonly int targetPid;
    private readonly bool includeTree;
    private IAudioClient? audioClient;
    private IAudioCaptureClient? captureClient;
    private volatile EventWaitHandle? bufferReady;
    private Thread? captureThread;
    private volatile bool running;
    private volatile bool stopRequested;
    private volatile bool threadExited;

    public WaveFormat WaveFormat { get; set; } = CaptureFormat;

    public event EventHandler<WaveInEventArgs>? DataAvailable;
    public event EventHandler<StoppedEventArgs>? RecordingStopped;

    /// <param name="processId">The PID whose render audio to capture.</param>
    /// <param name="includeProcessTree">Capture the PID and all of its descendant processes
    /// (INCLUDE tree) — the normal choice, since browsers and media apps spawn child renderers.</param>
    public ProcessLoopbackCapture(int processId, bool includeProcessTree = true)
    {
        targetPid = processId;
        includeTree = includeProcessTree;
    }

    /// <summary>Test-only override: when set, <see cref="IsSupported"/> returns this instead of the real OS
    /// check, so a self-test running on Windows 10/11 can exercise the Windows-7 (unsupported) code paths —
    /// e.g. the send-mode UI that crashed at launch on Win7 (issue #22). Null = use the real OS check.</summary>
    internal static bool? ForceSupportedForTest;

    /// <summary>Diagnostic sink for activation-path tracing (hr codes, callback delivery, timing). Static
    /// so a probe can wire it without touching every construction site. Null = no tracing.</summary>
    internal static Action<string>? Diagnostic;

    /// <summary>True on Windows builds new enough for the process-loopback API (10.0.19041+).</summary>
    public static bool IsSupported =>
        ForceSupportedForTest ?? OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041);

    public void StartRecording()
    {
        if (running) return;
        if (!IsSupported)
            throw new PlatformNotSupportedException("Process-loopback capture needs Windows 10 build 19041 or newer.");

        running = true;
        stopRequested = false;
        threadExited = false;
        // The capture thread owns the ENTIRE COM lifecycle — it activates, runs, and releases every COM
        // object itself before exiting. Nothing else ever touches those objects, so they can never be
        // released out from under a running native call (the access-violation hard-crash we hit when a
        // rebuild disposed the capture mid-GetBuffer). Activation is done on this thread too, so blocking
        // on the async-activation callback can't stall the UI thread.
        captureThread = new Thread(CaptureThreadMain)
        {
            IsBackground = true,
            Name = $"proc-loopback-{targetPid}",
            Priority = ThreadPriority.AboveNormal,
        };
        // WASAPI / ActivateAudioInterfaceAsync want an MTA thread: the completion callback arrives on an
        // MTA pool thread and we block waiting for it, so this must not be the STA UI thread.
        captureThread.SetApartmentState(ApartmentState.MTA);
        captureThread.Start();
    }

    public void StopRecording()
    {
        var t = captureThread;
        captureThread = null;
        running = false;
        stopRequested = true;
        bufferReady?.Set(); // wake the loop so it exits promptly
        // Only WAIT for the thread here; never release COM from this side. If the thread is wedged in a
        // native call and doesn't return, we leak it rather than free objects from another thread and
        // risk an access violation — a rare leak beats a hard crash. Guard against joining ourselves in
        // case a RecordingStopped handler re-enters.
        if (t is not null && t != Thread.CurrentThread) t.Join(2000);
    }

    private void Activate()
    {
        // Build the activation params: process-loopback for our PID with the include-tree mode.
        var loopbackParams = new AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS
        {
            TargetProcessId = (uint)targetPid,
            ProcessLoopbackMode = includeTree
                ? PROCESS_LOOPBACK_MODE.INCLUDE_TARGET_PROCESS_TREE
                : PROCESS_LOOPBACK_MODE.EXCLUDE_TARGET_PROCESS_TREE,
        };

        var activationParams = new AUDIOCLIENT_ACTIVATION_PARAMS
        {
            ActivationType = AUDIOCLIENT_ACTIVATION_TYPE.PROCESS_LOOPBACK,
            ProcessLoopbackParams = loopbackParams,
        };

        var paramsSize = Marshal.SizeOf<AUDIOCLIENT_ACTIVATION_PARAMS>();
        var paramsPtr = Marshal.AllocHGlobal(paramsSize);
        var propVariant = default(PROPVARIANT);
        try
        {
            Marshal.StructureToPtr(activationParams, paramsPtr, false);
            propVariant.vt = 65; // VT_BLOB
            propVariant.blobSize = (uint)paramsSize;
            propVariant.blobData = paramsPtr;

            var iidAudioClient = typeof(IAudioClient).GUID;
            var handler = new ActivationHandler();
            // The out operation is taken as a raw pointer, NOT the typed interface: casting the not-yet-
            // completed operation object threw here. We don't need it — the completion handler carries the
            // result — so just hold and release the reference.
            var opPtr = IntPtr.Zero;
            try
            {
                var hr = ActivateAudioInterfaceAsync(VirtualDevicePath, ref iidAudioClient, ref propVariant, handler, out opPtr);
                Diagnostic?.Invoke($"activate: ActivateAudioInterfaceAsync hr=0x{hr:X8} for pid {targetPid}");
                if (hr != 0) Marshal.ThrowExceptionForHR(hr);

                if (!handler.Completed.WaitOne(3000))
                    throw new TimeoutException("Process-loopback activation timed out (completion callback never arrived).");
                if (handler.ActivateResult != 0) Marshal.ThrowExceptionForHR(handler.ActivateResult);

                audioClient = (IAudioClient)handler.Interface!;
            }
            finally
            {
                if (opPtr != IntPtr.Zero) Marshal.Release(opPtr);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(paramsPtr);
        }

        var formatPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WaveFormat>());
        try
        {
            Marshal.StructureToPtr(CaptureFormat, formatPtr, false);
            var flags = AUDCLNT_STREAMFLAGS_LOOPBACK
                      | AUDCLNT_STREAMFLAGS_EVENTCALLBACK
                      | AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM;
            var hr = audioClient!.Initialize(AUDCLNT_SHAREMODE_SHARED, flags, BufferDurationHns, 0, formatPtr, IntPtr.Zero);
            if (hr != 0) Marshal.ThrowExceptionForHR(hr);
        }
        finally
        {
            Marshal.FreeHGlobal(formatPtr);
        }

        bufferReady = new EventWaitHandle(false, EventResetMode.AutoReset);
        var setHr = audioClient!.SetEventHandle(bufferReady.SafeWaitHandle.DangerousGetHandle());
        if (setHr != 0) Marshal.ThrowExceptionForHR(setHr);

        var iidCapture = typeof(IAudioCaptureClient).GUID;
        var svcHr = audioClient.GetService(ref iidCapture, out var svc);
        if (svcHr != 0) Marshal.ThrowExceptionForHR(svcHr);
        captureClient = (IAudioCaptureClient)svc;

        var startHr = audioClient.Start();
        if (startHr != 0) Marshal.ThrowExceptionForHR(startHr);
    }

    /// <summary>The whole life of one process-loopback capture, start to finish, on a single MTA thread:
    /// activate the client, pull audio until asked to stop (or an error), then release every COM object
    /// here — never from another thread. RecordingStopped fires exactly once when the thread finishes.</summary>
    private void CaptureThreadMain()
    {
        Exception? failure = null;
        try
        {
            Activate();      // creates audioClient / captureClient / bufferReady on THIS thread
            RunCaptureLoop();
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            TeardownComOnThisThread();
            running = false;
            threadExited = true;
            RecordingStopped?.Invoke(this, new StoppedEventArgs(failure));
        }
    }

    private void RunCaptureLoop()
    {
        var frameBytes = CaptureFormat.BlockAlign; // 8 bytes (2ch * float)
        while (!stopRequested)
        {
            if (bufferReady!.WaitOne(200) == false) continue;
            if (stopRequested) break;

            while (!stopRequested)
            {
                var hr = captureClient!.GetBuffer(out var dataPtr, out var frames, out var flags, out _, out _);
                if (hr != 0)
                {
                    // AUDCLNT_S_BUFFER_EMPTY (0x08890001) — nothing to read this wake.
                    if ((uint)hr == 0x08890001) break;
                    Marshal.ThrowExceptionForHR(hr);
                }
                if (frames == 0) break;

                var byteCount = frames * frameBytes;
                var buffer = new byte[byteCount];
                const int AUDCLNT_BUFFERFLAGS_SILENT = 0x2;
                if ((flags & AUDCLNT_BUFFERFLAGS_SILENT) == 0)
                    Marshal.Copy(dataPtr, buffer, 0, byteCount);
                // else leave zeroed — WASAPI signalled a silent packet.

                captureClient.ReleaseBuffer(frames);
                DataAvailable?.Invoke(this, new WaveInEventArgs(buffer, byteCount));
            }
        }
    }

    /// <summary>Releases the COM objects on the capture thread (the only thread that ever touches them).
    /// Stop the client before releasing so no callback is in flight.</summary>
    private void TeardownComOnThisThread()
    {
        try { audioClient?.Stop(); } catch { }
        try { audioClient?.Reset(); } catch { }
        if (captureClient != null) { try { Marshal.ReleaseComObject(captureClient); } catch { } captureClient = null; }
        if (audioClient != null) { try { Marshal.ReleaseComObject(audioClient); } catch { } audioClient = null; }
    }

    public void Dispose()
    {
        StopRecording();
        // Dispose the wait handle only once the thread has genuinely exited (it uses the handle). If the
        // thread wedged and StopRecording's join timed out, leave the handle alone rather than pull it
        // from under a live WaitOne — the leak is bounded and safe; a use-after-free would not be.
        if (threadExited)
        {
            bufferReady?.Dispose();
            bufferReady = null;
        }
    }

    // ---- Async activation completion handler -------------------------------------------------

    // Completion handler for ActivateAudioInterfaceAsync. TWO things here are load-bearing and were the
    // reason per-app capture NEVER worked on any machine (activation always "timed out"):
    //   1. ActivateCompleted takes the operation as a raw IntPtr, NOT the typed
    //      IActivateAudioInterfaceAsyncOperation. Typing it made the CLR QueryInterface the operation as
    //      the call was delivered; that QI returns E_NOINTERFACE, and it fails INSIDE the interop stub —
    //      before our method body — so the whole callback was silently dropped and the wait timed out.
    //   2. We read the result by calling GetActivateResult through the vtable directly (see below) rather
    //      than casting the pointer to our interface — the same QI that fails in (1).
    // The callback is delivered on an MTA worker thread; a plain WaitOne on the activation thread catches
    // it. (Apartment-agility via IAgileObject was tried and is NOT required — removed to avoid confusion.)
    [ComVisible(true)]
    private sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler
    {
        public readonly EventWaitHandle Completed = new(false, EventResetMode.ManualReset);
        public int ActivateResult { get; private set; } = unchecked((int)0x80004005); // E_FAIL until proven otherwise
        public object? Interface { get; private set; }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetActivateResultDelegate(IntPtr self, out int activateResult, out IntPtr activatedInterface);

        public void ActivateCompleted(IntPtr activateOperation)
        {
            Diagnostic?.Invoke($"ActivateCompleted ENTERED on thread apartment={Thread.CurrentThread.GetApartmentState()}, op={(activateOperation == IntPtr.Zero ? "null" : "set")}");
            var ifacePtr = IntPtr.Zero;
            try
            {
                // The pointer we're handed ALREADY IS an IActivateAudioInterfaceAsyncOperation* (that's the
                // API contract) — do NOT QueryInterface it (that QI returns E_NOINTERFACE here, cross-proxy).
                // Call GetActivateResult directly through vtable slot 3 (after IUnknown's 3 slots).
                var vtbl = Marshal.ReadIntPtr(activateOperation);
                var fnPtr = Marshal.ReadIntPtr(vtbl, 3 * IntPtr.Size);
                var getResult = Marshal.GetDelegateForFunctionPointer<GetActivateResultDelegate>(fnPtr);
                var callHr = getResult(activateOperation, out var activateHr, out ifacePtr);
                if (callHr != 0) { ActivateResult = callHr; Diagnostic?.Invoke($"ActivateCompleted: GetActivateResult call failed hr=0x{callHr:X8}"); return; }
                ActivateResult = activateHr;
                if (activateHr == 0 && ifacePtr != IntPtr.Zero)
                    Interface = Marshal.GetTypedObjectForIUnknown(ifacePtr, typeof(IAudioClient));
                Diagnostic?.Invoke($"ActivateCompleted: activateHr=0x{activateHr:X8}, iface={(ifacePtr == IntPtr.Zero ? "null" : "got")}");
            }
            catch (Exception ex)
            {
                ActivateResult = ex.HResult != 0 ? ex.HResult : unchecked((int)0x80004005);
                Diagnostic?.Invoke($"ActivateCompleted: THREW {ex.GetType().Name}: {ex.Message} (hr=0x{ActivateResult:X8})");
            }
            finally
            {
                if (ifacePtr != IntPtr.Zero) Marshal.Release(ifacePtr); // GetTypedObjectForIUnknown took its own ref
                Completed.Set();
            }
        }
    }

    // ---- Native declarations -----------------------------------------------------------------

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        ref Guid riid,
        ref PROPVARIANT activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IntPtr activationOperation);

    private enum AUDIOCLIENT_ACTIVATION_TYPE { DEFAULT = 0, PROCESS_LOOPBACK = 1 }

    private enum PROCESS_LOOPBACK_MODE { INCLUDE_TARGET_PROCESS_TREE = 0, EXCLUDE_TARGET_PROCESS_TREE = 1 }

    [StructLayout(LayoutKind.Sequential)]
    private struct AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS
    {
        public uint TargetProcessId;
        public PROCESS_LOOPBACK_MODE ProcessLoopbackMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AUDIOCLIENT_ACTIVATION_PARAMS
    {
        public AUDIOCLIENT_ACTIVATION_TYPE ActivationType;
        public AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS ProcessLoopbackParams;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPVARIANT
    {
        public ushort vt;
        public ushort r1, r2, r3;
        public uint blobSize;
        public IntPtr blobData;
        public IntPtr padding; // keep the union large enough on 64-bit
    }
}

// ---- COM interfaces (declared in vtable order — DO NOT reorder methods) -----------------------

// NB: IActivateAudioInterfaceAsyncOperation is deliberately NOT declared as a managed interface — its
// GetActivateResult is invoked through the vtable directly in ActivationHandler.ActivateCompleted,
// because QueryInterface-ing the operation pointer to a managed interface returns E_NOINTERFACE here.

[ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IActivateAudioInterfaceCompletionHandler
{
    // The operation is taken as a RAW pointer, NOT the typed interface. Typing it made the CLR marshal
    // (QueryInterface) the operation object as the call was delivered; that QI returns E_NOINTERFACE
    // here, and the failure happened INSIDE the interop stub — before our method body — so the callback
    // was silently dropped and activation always "timed out". With IntPtr the stub marshals nothing, the
    // method runs, and we QI the operation ourselves on this (the delivery) thread.
    void ActivateCompleted(IntPtr activateOperation);
}

[ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClient
{
    [PreserveSig] int Initialize(int shareMode, uint streamFlags, long hnsBufferDuration,
        long hnsPeriodicity, IntPtr format, IntPtr audioSessionGuid);
    [PreserveSig] int GetBufferSize(out uint bufferFrames);
    [PreserveSig] int GetStreamLatency(out long latency);
    [PreserveSig] int GetCurrentPadding(out uint padding);
    [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
    [PreserveSig] int GetMixFormat(out IntPtr deviceFormat);
    [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
    [PreserveSig] int Reset();
    [PreserveSig] int SetEventHandle(IntPtr eventHandle);
    [PreserveSig] int GetService(ref Guid interfaceId,
        [MarshalAs(UnmanagedType.IUnknown)] out object instance);
}

[ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioCaptureClient
{
    [PreserveSig] int GetBuffer(out IntPtr dataBuffer, out int framesToRead,
        out int flags, out long devicePosition, out long qpcPosition);
    [PreserveSig] int ReleaseBuffer(int framesRead);
    [PreserveSig] int GetNextPacketSize(out int framesInNextPacket);
}
