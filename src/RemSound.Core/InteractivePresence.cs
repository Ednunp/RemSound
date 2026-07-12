// Lets the in-app self-tests (RemSound.App) reach the name-parameterised test seams below, which
// isolate a test from a real running RemSound holding the production presence token.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("RemSound")]

namespace RemSound.Core;

/// <summary>
/// Cross-session coordination between the interactive RemSound app and the send-only Windows service.
/// The app holds a global token for its whole lifetime; the service watches the token and YIELDS —
/// suspends its own sending — whenever the app is present, resuming when the app closes. Because the
/// token is a named mutex, Windows releases it automatically if the app CRASHES, so the service always
/// recovers on its own (no stuck "app is running" state).
///
/// <para>The name lives in the <c>Global\</c> namespace so it's visible across Terminal Services
/// sessions — the service runs in session 0, the app in the interactive session.</para>
/// </summary>
public static class InteractivePresence
{
    private const string MutexName = @"Global\RemSound.Interactive.v1";

    /// <summary>Called ONCE by the interactive app at startup. Acquires and holds the presence token
    /// (on a dedicated thread, so ownership isn't tied to the UI thread and release is crash-safe) until
    /// the returned handle is disposed or the process exits. Returns null if the token couldn't be
    /// acquired — the app then simply runs without a hold (worst case the service doesn't yield to it).
    /// Never throws, never blocks the app for more than a few seconds.</summary>
    public static IDisposable? AcquireHold() => AcquireHold(MutexName);

    /// <summary>Testable overload against a caller-supplied token name so a test never collides with a
    /// real running app holding the production token.</summary>
    internal static IDisposable? AcquireHold(string name)
    {
        var hold = new Hold(name);
        return hold.Start() ? hold : null;
    }

    /// <summary>Called by the service. True when an interactive RemSound app is currently running.
    /// Works by trying to take the same token briefly: if the app holds it we can't, so it's present;
    /// if we take it (or find it abandoned = the app crashed) we release it again immediately and report
    /// "not present". The service must never end up holding the token itself.</summary>
    public static bool IsInteractiveAppRunning() => IsInteractiveAppRunning(MutexName);

    /// <summary>Testable overload — see <see cref="AcquireHold(string)"/>.</summary>
    internal static bool IsInteractiveAppRunning(string name)
    {
        System.Threading.Mutex? mutex = null;
        try
        {
            if (!System.Threading.Mutex.TryOpenExisting(name, out mutex) || mutex is null)
                return false; // nobody ever created it → no app has run

            bool acquired;
            try { acquired = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; } // app crashed → token abandoned

            if (acquired)
            {
                try { mutex.ReleaseMutex(); } catch { /* only if we own it */ }
                return false; // we could take it → the app is not holding it
            }
            return true; // couldn't take it → the app holds it → app present
        }
        catch { return false; }
        finally { mutex?.Dispose(); }
    }

    /// <summary>Holds the mutex on a dedicated background thread for the app's lifetime. Acquiring and
    /// releasing on the SAME thread sidesteps the mutex's thread-affinity rule; the thread survives until
    /// Dispose (or process exit, which frees the OS handle either way).</summary>
    private sealed class Hold : IDisposable
    {
        private readonly string name;
        private readonly ManualResetEventSlim acquiredSignal = new(false);
        private readonly ManualResetEventSlim stopSignal = new(false);
        private volatile bool ok;
        private Thread? thread;

        public Hold(string name) => this.name = name;

        public bool Start()
        {
            thread = new Thread(Run) { IsBackground = true, Name = "remsound-presence" };
            thread.Start();
            acquiredSignal.Wait(6000);
            return ok;
        }

        private void Run()
        {
            System.Threading.Mutex? m = null;
            try
            {
                m = new System.Threading.Mutex(false, name, out _);
                bool owned;
                try { owned = m.WaitOne(TimeSpan.FromSeconds(5)); }
                catch (AbandonedMutexException) { owned = true; }
                ok = owned;
                acquiredSignal.Set();
                if (!owned) return;
                stopSignal.Wait();                    // hold the token until disposed
                try { m.ReleaseMutex(); } catch { }
            }
            catch
            {
                ok = false;
                acquiredSignal.Set();
            }
            finally { try { m?.Dispose(); } catch { } }
        }

        public void Dispose()
        {
            stopSignal.Set();
            try { thread?.Join(2000); } catch { }
            acquiredSignal.Dispose();
            stopSignal.Dispose();
        }
    }
}
