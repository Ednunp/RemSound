using System.Runtime.CompilerServices;

namespace RemSound.App;

/// <summary>
/// Feature-detects whether the send-only Windows service can be offered on THIS machine — by actually
/// trying to load the service machinery, not by checking a Windows version number.
///
/// <para>Why feature-detect instead of "Windows 10 or newer": Windows 7 has a full Service Control Manager,
/// so the service could work there IF the .NET service layer (<c>System.ServiceProcess</c>) loads on it.
/// That's the real unknown — .NET 10 isn't officially supported on Win7 — so we simply try, and offer the
/// service wherever the attempt succeeds (Win7 included) while hiding it safely wherever it doesn't.</para>
///
/// <para>The load attempt is isolated in <see cref="Probe"/> and marked NoInlining on purpose: the reference
/// to the <c>System.ServiceProcess</c> types lives ONLY there, so the assembly load is triggered by the
/// <em>call</em> to Probe (inside <see cref="IsAvailable"/>'s try/catch) and is therefore catchable — instead
/// of happening when IsAvailable itself is compiled, which would be an unrecoverable crash on an OS where
/// that assembly won't load. This is the same failure that took the app down at launch before the startup
/// path was made service-type-free; here it degrades to "hide the menu" instead.</para>
/// </summary>
internal static class ServiceCapability
{
    private static bool? cached;

    /// <summary>True when the Windows service machinery loads and is usable on this OS/runtime. Cached after
    /// the first check; never throws.</summary>
    public static bool IsAvailable()
    {
        if (cached is { } c) return c;
        bool ok;
        try { ok = Probe(); }
        catch { ok = false; } // System.ServiceProcess couldn't load here — offer nothing, stay safe
        cached = ok;
        return ok;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool Probe()
    {
        // Constructing a ServiceController forces the System.ServiceProcess assembly to load — the exact
        // step that fails on an OS the .NET service layer doesn't support. The PARAMETERLESS ctor touches
        // no service and no Service Control Manager, so it can't throw for a benign reason (naming a made-up
        // service and reading a property WOULD hit the SCM and throw "not found"). The construction succeeding
        // is all we need to know the machinery loads here; a real service is opened later via ServiceControl.
        using var probe = new System.ServiceProcess.ServiceController();
        return true;
    }
}
