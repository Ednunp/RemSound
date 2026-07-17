using System.Runtime.InteropServices;

namespace RemSound.Sender;

/// <summary>
/// A dedicated STA thread with a Windows message pump, used to own the ENTIRE lifecycle of one ASIO
/// driver — create, initialise, start, stop and (crucially) dispose. ASIO drivers are COM objects that
/// generally require every one of their control calls to be made from a single thread, and several
/// (notably Audient) crash NATIVELY on close when that isn't honoured, or when there is no message pump
/// running to service the messages the driver posts during init/reset/close. The previous code made
/// these calls on whatever thread happened to call Start/Stop, with only a Sleep() before the close —
/// which is why closing the driver (on "ASIO → none" or a driver switch) could take the whole process
/// down with an access violation that left no managed stack.
///
/// <para>Route every AsioOut control call through <see cref="Invoke"/> and the driver gets a stable home
/// thread and a live pump. The ASIO audio callback still runs on the driver's own real-time thread — that
/// is unchanged; only the control calls move here.</para>
/// </summary>
internal sealed class AsioApartment : IDisposable
{
    private readonly Thread thread;
    private readonly ManualResetEventSlim started = new(false);
    private readonly object queueGate = new();
    private readonly Queue<WorkItem> queue = new();
    private readonly AutoResetEvent workAvailable = new(false);
    private volatile bool shutdown;

    private sealed class WorkItem
    {
        public required Action Action;
        public required ManualResetEventSlim Done;
        public Exception? Error;
    }

    public AsioApartment(string name = "asio-control")
    {
        thread = new Thread(Run) { IsBackground = true, Name = name };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        started.Wait(); // don't accept Invoke()s until the pump is up
    }

    /// <summary>Run <paramref name="action"/> on the apartment thread and block until it finishes,
    /// rethrowing anything it raised. If called from the apartment thread itself, or after Dispose, runs
    /// inline so a teardown path can never deadlock on itself.</summary>
    public void Invoke(Action action)
    {
        if (shutdown || Thread.CurrentThread == thread) { action(); return; }
        var item = new WorkItem { Action = action, Done = new ManualResetEventSlim(false) };
        lock (queueGate) queue.Enqueue(item);
        workAvailable.Set();
        item.Done.Wait();
        item.Done.Dispose();
        if (item.Error is not null) throw item.Error;
    }

    private void Run()
    {
        // Force a message queue to exist on this thread before anyone posts to it.
        PeekMessage(out _, IntPtr.Zero, 0, 0, PM_NOREMOVE);
        started.Set();

        var handles = new[] { workAvailable.SafeWaitHandle.DangerousGetHandle() };
        while (!shutdown)
        {
            // Wake on either queued work or an incoming window message, so the driver's posted messages
            // are dispatched promptly (that servicing is what several ASIO drivers need to close cleanly).
            MsgWaitForMultipleObjects(1, handles, false, INFINITE, QS_ALLINPUT);

            while (PeekMessage(out var msg, IntPtr.Zero, 0, 0, PM_REMOVE))
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }

            while (true)
            {
                WorkItem? item;
                lock (queueGate) item = queue.Count > 0 ? queue.Dequeue() : null;
                if (item is null) break;
                try { item.Action(); }
                catch (Exception ex) { item.Error = ex; }
                finally { item.Done.Set(); }
            }
        }
    }

    public void Dispose()
    {
        if (shutdown) return;
        shutdown = true;
        workAvailable.Set();
        try { if (thread.IsAlive && Thread.CurrentThread != thread) thread.Join(2000); } catch { /* leaked thread beats a hang */ }
        try { workAvailable.Dispose(); } catch { }
        try { started.Dispose(); } catch { }
    }

    // ---- native message pump ----
    private const uint QS_ALLINPUT = 0x04FF;
    private const uint INFINITE = 0xFFFFFFFF;
    private const uint PM_REMOVE = 0x0001;
    private const uint PM_NOREMOVE = 0x0000;

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("user32.dll")]
    private static extern uint MsgWaitForMultipleObjects(uint nCount, IntPtr[] pHandles, bool bWaitAll, uint dwMilliseconds, uint dwWakeMask);

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);
}
