using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RemSound.App;

/// <summary>
/// Enforces "only one RemSound at a time" and provides the plumbing to either surface the
/// already-running copy or force a stuck one to close. Created once in Program.Main and held
/// for the whole app lifetime.
///
/// Why this exists: RemSound had no single-instance guard at all — it only ever NOTICED a
/// second copy when a global hotkey failed to register. With the auto-updater relaunching the
/// app, a copy that didn't exit cleanly could leave two (then more) copies running at once,
/// each playing received audio. Andre hit exactly that on 2026-05-30: copies "stacked and
/// stacked", the audio got deafening, and the only way out was force-killing them all from a
/// terminal. A real lock makes that structurally impossible.
///
/// Mechanism:
///   * A named system <see cref="Mutex"/> is the lock. The first copy to start owns it; a
///     later copy fails to acquire it and so KNOWS another copy is live.
///   * A named auto-reset <see cref="EventWaitHandle"/> is the "come to the front" signal.
///     The owning copy runs a background thread waiting on it; when a second copy sets it
///     (the user chose "switch to the running copy"), the thread raises
///     <see cref="ActivateRequested"/>, which Program.Main routes to the live window.
///   * A second named event is the "please close" signal behind <c>RemSound --close</c>. The same
///     thread raises <see cref="CloseRequested"/>, and the window closes the way File, Exit does.
///   * <see cref="ForceCloseOtherInstances"/> terminates any other RemSound process with
///     Process.Kill (TerminateProcess), so a hung copy dies regardless of its message-loop
///     state. If a copy is running elevated and we are not, the kill is retried via an
///     elevated taskkill (one UAC prompt).
///
/// Names are in the per-session (Local) namespace and are fixed strings — not version- or
/// path-derived — so ANY RemSound.exe blocks ANY other, which is the "refuses to load unless
/// previous copies are gone" guarantee Andre asked for. 2026-05-31.
/// </summary>
internal sealed class SingleInstanceCoordinator : IDisposable
{
    private const string MutexName = "RemSound.SingleInstance.Mutex.v1";
    private const string ActivateEventName = "RemSound.SingleInstance.Activate.v1";
    private const string CloseEventName = "RemSound.SingleInstance.Close.v1";

    private readonly Mutex mutex;
    private readonly string activateEventName;
    private readonly string closeEventName;
    private bool ownsMutex;
    private EventWaitHandle? activateEvent;
    private EventWaitHandle? closeEvent;
    private Thread? listenerThread;
    private volatile bool stopListener;

    /// <summary>Raised on a background thread when another copy asks this one to surface.
    /// Program.Main marshals it onto the running window.</summary>
    public event Action? ActivateRequested;

    /// <summary>Raised on a background thread when <c>RemSound --close</c> asks this copy to close.
    /// Program.Main routes it to the running window.</summary>
    public event Action? CloseRequested;

    public SingleInstanceCoordinator() : this(MutexName, ActivateEventName, CloseEventName) { }

    /// <summary>Explicit names, for the gate: a test that used the real names would reach the RemSound the user
    /// has open — and asking that one to close is exactly what the close signal does.</summary>
    internal SingleInstanceCoordinator(string mutexName, string activateEventName, string closeEventName)
    {
        mutex = new Mutex(initiallyOwned: false, mutexName);
        this.activateEventName = activateEventName;
        this.closeEventName = closeEventName;
    }

    /// <summary>Try to take the single-instance lock, waiting up to <paramref name="timeout"/>.
    /// An abandoned mutex (previous owner crashed or was force-killed) counts as acquired — we
    /// become the new owner.</summary>
    public bool TryAcquire(TimeSpan timeout)
    {
        if (ownsMutex) return true;
        try
        {
            ownsMutex = mutex.WaitOne(timeout);
        }
        catch (AbandonedMutexException)
        {
            // Previous owner died without releasing. Ownership passes to us.
            ownsMutex = true;
        }
        return ownsMutex;
    }

    /// <summary>Start the background listener that surfaces this copy when a later copy
    /// signals it, and closes it when <c>--close</c> asks. Only meaningful on the primary instance.</summary>
    public void StartActivationListener()
    {
        // Can't create the activate signal — "switch to the running copy" just won't surface this
        // window automatically. Not fatal; the user can still reach it via the tray.
        try { activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, activateEventName); }
        catch { activateEvent = null; }
        // Can't create the close signal — --close then ends the process outright, as it always used to.
        try { closeEvent = new EventWaitHandle(false, EventResetMode.AutoReset, closeEventName); }
        catch { closeEvent = null; }
        if (activateEvent is null && closeEvent is null) return;
        listenerThread = new Thread(ListenLoop) { IsBackground = true, Name = "RemSound-Activation" };
        listenerThread.Start();
    }

    private void ListenLoop()
    {
        var handles = new List<WaitHandle>(2);
        if (activateEvent is not null) handles.Add(activateEvent);
        if (closeEvent is not null) handles.Add(closeEvent);
        var waitOn = handles.ToArray();
        while (!stopListener)
        {
            try
            {
                // Short timeout so Dispose can stop us promptly even if no signal arrives.
                var which = WaitHandle.WaitAny(waitOn, 500);
                if (which == WaitHandle.WaitTimeout || stopListener) continue;
                if (ReferenceEquals(waitOn[which], closeEvent)) CloseRequested?.Invoke();
                else ActivateRequested?.Invoke();
            }
            catch
            {
                return;
            }
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);

    /// <summary>Signal whichever copy currently owns the lock to bring itself to the front.
    /// Called by a SECOND copy that chose "switch to the running copy".
    ///
    /// Windows blocks <c>SetForegroundWindow</c> from a process that didn't receive the most
    /// recent user input. When the user launches this second copy from (say) an Explorer
    /// window and clicks "switch", the input belongs to US, not the running copy — so the
    /// running copy's own SetForegroundWindow is denied and its window only flashes in the
    /// taskbar instead of coming forward (the bug Ed reported). The documented fix is for the
    /// process that currently holds the foreground right — us, right now — to hand it to the
    /// running copy via <see cref="AllowSetForegroundWindow"/> BEFORE signalling. We then
    /// linger briefly so the running copy can raise itself while the grant is fresh and the
    /// foreground hasn't churned from us exiting.</summary>
    public static void SignalExistingToActivate()
    {
        // Grant every other RemSound process the one-shot right to pull itself to the front.
        foreach (var p in OtherInstances(Environment.ProcessId))
        {
            try { AllowSetForegroundWindow(p.Id); } catch { /* best-effort */ }
            p.Dispose();
        }

        try
        {
            if (EventWaitHandle.TryOpenExisting(ActivateEventName, out var ev))
            {
                using (ev) ev.Set();
            }
        }
        catch
        {
            // Best-effort — if the signal can't be delivered the user can click the tray icon.
        }

        // Stay alive a beat so the running copy can raise itself while we're still the
        // foreground process and the grant is fresh. If we vanished instantly the foreground
        // would churn and the grant could be consumed before it's used. Invisible to the user —
        // our dialog has already closed and we have no window.
        try { Thread.Sleep(600); } catch { /* ignore */ }
    }

    /// <summary>
    /// Ask the running copy to close the way File, Exit does. Returns true when a running copy was listening for the
    /// request — which says nothing yet about whether it has closed; wait with <see cref="WaitForOtherInstancesToExit"/>.
    ///
    /// <para><c>RemSound --close</c> used to go straight to <see cref="ForceCloseOtherInstances"/>: an ended process leaves a
    /// recording without its ending, the router's port mapping in place and the log without its last line. 2026-09-13
    /// review.</para>
    /// </summary>
    public static bool RequestGracefulClose() => RequestGracefulClose(CloseEventName);

    internal static bool RequestGracefulClose(string closeEventName)
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(closeEventName, out var ev)) return false;
            using (ev) ev.Set();
            return true;
        }
        catch { return false; }
    }

    /// <summary>True when another RemSound this guard could close is running in this Windows session.</summary>
    public static bool AnyOtherInstanceRunning()
    {
        var others = OtherInstances(Environment.ProcessId);
        foreach (var p in others) p.Dispose();
        return others.Count > 0;
    }

    /// <summary>Wait up to <paramref name="timeout"/> for every other RemSound in this session to exit. True when none is left.</summary>
    public static bool WaitForOtherInstancesToExit(TimeSpan timeout)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (true)
        {
            if (!AnyOtherInstanceRunning()) return true;
            if (Environment.TickCount64 >= deadline) return false;
            Thread.Sleep(250);
        }
    }

    /// <summary>Force every OTHER RemSound process to terminate. Returns true if no other
    /// RemSound process remains after the attempt. Uses Process.Kill (TerminateProcess) so a
    /// hung copy dies regardless of its state; PIDs we can't reach (an elevated copy while we
    /// run normally) are retried via an elevated taskkill.</summary>
    public static bool ForceCloseOtherInstances()
    {
        var me = Environment.ProcessId;
        var deniedPids = new List<int>();

        foreach (var p in OtherInstances(me))
        {
            try
            {
                p.Kill();
                p.WaitForExit(4000);
            }
            catch (Win32Exception)
            {
                // Access denied — almost always an elevated target we can't reach unelevated.
                deniedPids.Add(p.Id);
            }
            catch (InvalidOperationException)
            {
                // Already exited between enumeration and Kill — fine.
            }
            catch
            {
                // Ignore — the post-check below is the source of truth.
            }
            finally
            {
                p.Dispose();
            }
        }

        if (deniedPids.Count > 0)
        {
            TryElevatedKill(deniedPids);
        }

        // Source of truth: is the field actually clear now?
        var remaining = OtherInstances(me).ToList();
        var clear = remaining.Count == 0;
        foreach (var p in remaining) p.Dispose();
        return clear;
    }

    /// <summary>
    /// May the single-instance guard kill this process?
    ///
    /// <para>Only a copy in OUR OWN Windows session. The mutex above is created without a
    /// <c>Global\</c> prefix, so it lives in the per-session namespace — nothing outside this session
    /// can possibly be holding it, and killing something out there could never resolve the
    /// conflict.</para>
    ///
    /// <para>The reason this matters is the SERVICE. It runs the same RemSound.exe, so its process is
    /// also called "RemSound", and it sits in session 0. Enumerating by name alone picked it up, and
    /// "force close the other copy" then tried to terminate it: access denied unelevated, which routed
    /// it into the elevated taskkill and put a UAC prompt in front of somebody who had asked to close
    /// an app. Accepting it kills the lock-screen service — which the SCM then restarts, so the "is the
    /// field clear now?" check could also come back false and refuse to start at all. The service
    /// deliberately never takes the interactive lock, so it was never the cause of the dialog in the
    /// first place. Confirmed against a live install, 2026-08-24.</para>
    ///
    /// <para>A session id we cannot read means DON'T kill: never terminate a process on a guess.</para>
    /// </summary>
    internal static bool IsKillableInstance(int pid, int selfPid, int? sessionId, int ownSessionId)
    {
        if (pid == selfPid) return false;
        if (sessionId is null) return false;
        return sessionId.Value == ownSessionId;
    }

    private static List<Process> OtherInstances(int selfPid)
    {
        Process[] all;
        try { all = Process.GetProcessesByName("RemSound"); }
        catch { return []; }

        int ownSession;
        using (var self = Process.GetCurrentProcess())
        {
            try { ownSession = self.SessionId; }
            catch { foreach (var p in all) p.Dispose(); return []; }   // can't tell ours from theirs: kill nothing
        }

        var others = new List<Process>(all.Length);
        foreach (var p in all)
        {
            int? session;
            try { session = p.SessionId; }
            catch { session = null; }
            if (!IsKillableInstance(p.Id, selfPid, session, ownSession)) { p.Dispose(); continue; }
            others.Add(p);
        }
        return others;
    }

    private static void TryElevatedKill(List<int> pids)
    {
        try
        {
            // Target exact PIDs, never /IM RemSound.exe — an image-name kill would also take
            // out this very process. Verb=runas raises the one UAC prompt that lets a normal
            // process terminate an elevated one.
            var args = "/F " + string.Join(" ", pids.ConvertAll(id => $"/PID {id}"));
            var psi = new ProcessStartInfo("taskkill.exe", args)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true,
            };
            var proc = Process.Start(psi);
            proc?.WaitForExit(5000);
            proc?.Dispose();
        }
        catch
        {
            // User declined UAC, or taskkill wasn't available. The caller's post-check reports
            // the field still isn't clear and the UI surfaces a message.
        }
    }

    public void Dispose()
    {
        stopListener = true;
        try { activateEvent?.Set(); } catch { /* wake the listener so it can exit */ }
        try { listenerThread?.Join(1000); } catch { /* ignore */ }
        try { activateEvent?.Dispose(); } catch { /* ignore */ }
        try { closeEvent?.Dispose(); } catch { /* ignore */ }
        if (ownsMutex)
        {
            try { mutex.ReleaseMutex(); } catch { /* ignore */ }
        }
        try { mutex.Dispose(); } catch { /* ignore */ }
    }
}
