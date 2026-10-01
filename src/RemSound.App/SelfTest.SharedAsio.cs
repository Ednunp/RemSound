using RemSound.Core;

namespace RemSound.App;

internal static partial class SelfTest
{
    /// <summary>SHARED ASIO DEVICE, WITHOUT HARDWARE (2026-09-28, from the review of every step). The real-driver step is
    /// skipped in every gate that has no driver named, so the part every machine relies on - one instance per driver,
    /// shared by every holder, let go by the last, and a driver that will not open left unopened for a later try - was
    /// never run. None of that needs a driver: a name that cannot exist is enough.</summary>
    private static string? SharedAsioDeviceIsSharedAndLetGoByTheLast()
    {
        var name = "RemSound self-test — no such ASIO driver " + Guid.NewGuid().ToString("N")[..6];
        var lines = new List<string>();
        void Log(string line) { lock (lines) lines.Add(line); }

        var first = SharedAsioDevice.Acquire(name, Log);
        var second = SharedAsioDevice.Acquire(name, Log);
        var third = SharedAsioDevice.Acquire(name.ToUpperInvariant(), Log);
        Check(ReferenceEquals(first, second) && ReferenceEquals(first, third),
            "every holder of one driver must share ONE instance - two COM instances of one driver in one process is what crashed (names match whatever their case)");
        Check(!first.IsOpen && !SharedAsioDevice.IsOpenFor(name), "holding a driver opens nothing until a side attaches");

        // A driver that will not open: the attach fails, and the device is left unopened so a later attach can try again.
        var threw = false;
        try { first.EnsureOpen(); } catch { threw = true; }
        Check(threw && !first.IsOpen, "a driver that will not open must fail the attach and stay unopened");
        threw = false;
        try { first.EnsureOpen(); } catch { threw = true; }
        Check(threw && !first.IsOpen, "and a second try must try again (and fail again), not remember the first failure as success");
        lock (lines) Check(lines.Count(l => l.Contains("asio open: creating driver", StringComparison.Ordinal)) == 2,
            $"each try must really try to create the driver ({lines.Count(l => l.Contains("creating driver", StringComparison.Ordinal))} tries logged)");

        // Let go, holder by holder: only the last one closes it and takes it out of the register.
        first.Release(Log, closeTimeoutMs: 2000);
        Check(ReferenceEquals(SharedAsioDevice.Acquire(name, null), second), "while holders remain, the same instance must still be the one handed out");
        second.Release(null, closeTimeoutMs: 2000);   // the extra hold just taken
        second.Release(Log, closeTimeoutMs: 2000);
        third.Release(Log, closeTimeoutMs: 2000);
        var fresh = SharedAsioDevice.Acquire(name, null);
        try
        {
            Check(!ReferenceEquals(fresh, first), "once the last holder has let go, the driver must be closed and a new hold must get a fresh instance");
            Check(!fresh.IsOpen, "and a fresh hold opens nothing either");
        }
        finally { fresh.Release(null, closeTimeoutMs: 2000); }
        return "one instance per driver, whatever the case of its name, shared by every holder; nothing opened until a side attaches; "
             + "a driver that will not open is tried again on each attach; only the last holder letting go closes it";
    }
}
