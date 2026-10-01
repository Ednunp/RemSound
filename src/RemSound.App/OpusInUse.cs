using Concentus;

namespace RemSound.App;

/// <summary>
/// Which Opus RemSound's codec calls actually reach: the native libopus, or Concentus's managed C# fallback.
///
/// <para>Concentus looks for native libopus with a plain LoadLibrary("opus.dll") - beside RemSound.exe, in System32, on
/// the PATH - and on its first use copies the file from <c>runtimes\win-x64\native\</c> RELATIVE TO THE CURRENT FOLDER.
/// The package puts opus.dll only in that subfolder, so a copy first started from its own folder (a double-click) went
/// native for good, and one first started from anywhere else (at sign-in, by a script, by the gate) used the managed
/// fallback: compatible, but heavier, and its repair of lost packets comes out about 17 cents off pitch (measured in the
/// simulator, 2026-09-28). Every publish now puts opus.dll beside the exe (RemSound.App.csproj), and this says which one
/// is in use, in the app's log and the service's.</para>
/// </summary>
internal static class OpusInUse
{
    private static readonly Lazy<bool> native = new(() =>
    {
        try
        {
            var decoder = OpusCodecFactory.CreateDecoder(48000, 2, TextWriter.Null);
            var name = decoder.GetType().Name;
            (decoder as IDisposable)?.Dispose();
            return name.Contains("Native", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    });

    /// <summary>The native libopus is the one in use.</summary>
    internal static bool IsNative => native.Value;

    /// <summary>For the log: which Opus, and where the native one should be when it is not in use.</summary>
    internal static string Describe() => IsNative
        ? "native libopus in use"
        : $"Concentus's managed fallback in use - opus.dll was not found beside {Path.Combine(AppContext.BaseDirectory, "RemSound.exe")}";
}
