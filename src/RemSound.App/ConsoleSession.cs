using System.Runtime.InteropServices;
using System.Security.Principal;

namespace RemSound.App;

/// <summary>Is anybody signed in at this machine's console? The service's boot ear (<see cref="ServiceSendHost"/>) listens
/// only until somebody is. Windows XP and later, so Windows 7 too. Never throws: when Windows cannot say, the answer is
/// "yes", which leaves the service capturing as it always did.
///
/// Signing in counts. Ed's 30 Sep restart: Windows let him in by itself 10.1 s after it started, and the console only had
/// his name from about 10.7 s, once his profile had loaded. The service asked in between, heard "nobody", opened the boot
/// ear and swapped back 46 ms later, and the swap cost part of the startup sound. Windows' own record of who has been let
/// in (the logon sessions) has him from the moment he is let in.</summary>
internal static class ConsoleSession
{
    public static bool SomeoneSignedIn() => State() != SignIn.Nobody;

    /// <summary>For the log: "yes", "signing in" (let in, the desktop still loading) or "no".</summary>
    public static string Describe() => State() switch
    {
        SignIn.SignedIn => "yes",
        SignIn.SigningIn => "signing in (Windows has let somebody in; their desktop is still loading)",
        _ => "no",
    };

    private enum SignIn { Nobody, SigningIn, SignedIn }

    private static SignIn State()
    {
        try
        {
            var session = WTSGetActiveConsoleSessionId();
            if (session == NoSession) return SignIn.Nobody; // between sessions (a switch in progress): nobody at the console
            if (!WTSQuerySessionInformation(IntPtr.Zero, session, WtsUserName, out var buffer, out var bytes)) return SignIn.SignedIn;
            bool named;
            try { named = bytes > sizeof(char) && !string.IsNullOrEmpty(Marshal.PtrToStringUni(buffer)); }
            finally { WTSFreeMemory(buffer); }
            if (named && !HideNameForTest) return SignIn.SignedIn;
            return SomeoneLetInAt(session) ? SignIn.SigningIn : SignIn.Nobody;
        }
        catch { return SignIn.SignedIn; }
    }

    /// <summary>Pure: is this logon session a person let in at the console? Only a person's own account counts (a local or
    /// domain account, S-1-5-21-, or a work account, S-1-12-1-): Windows' own desktop and font sessions for the lock screen
    /// (S-1-5-90-, S-1-5-96-) log on as "interactive" too, before anybody is there.</summary>
    internal static bool IsPersonLetIn(uint logonType, uint session, uint consoleSession, string? sid) =>
        session == consoleSession
        && logonType is Interactive or RemoteInteractive or CachedInteractive or CachedRemoteInteractive
        && sid is not null
        && (sid.StartsWith("S-1-5-21-", StringComparison.Ordinal) || sid.StartsWith("S-1-12-1-", StringComparison.Ordinal));

    /// <summary>Has Windows let a person in at this console session? The service runs as SYSTEM and sees every logon session;
    /// anyone else sees only their own, which is enough to find themselves.</summary>
    internal static bool SomeoneLetInAt(uint consoleSession)
    {
        if (LsaEnumerateLogonSessions(out var count, out var luids) != 0) return false;
        try
        {
            for (var i = 0; i < count; i++)
            {
                if (LsaGetLogonSessionData(luids + i * Marshal.SizeOf<Luid>(), out var data) != 0 || data == IntPtr.Zero) continue;
                try
                {
                    var s = Marshal.PtrToStructure<LogonSessionData>(data);
                    var sid = s.Sid == IntPtr.Zero ? null : new SecurityIdentifier(s.Sid).Value;
                    if (IsPersonLetIn(s.LogonType, s.Session, consoleSession, sid)) return true;
                }
                finally { LsaFreeReturnBuffer(data); }
            }
            return false;
        }
        finally { LsaFreeReturnBuffer(luids); }
    }

    /// <summary>Test seam: the console's name not there yet, as while a profile is loading, so a person running the test is
    /// only signing in.</summary>
    internal static bool HideNameForTest;

    /// <summary>Test seam: the console session this machine has now.</summary>
    internal static uint ConsoleSessionIdForTest => WTSGetActiveConsoleSessionId();

    private const uint NoSession = 0xFFFFFFFF;
    private const int WtsUserName = 5; // WTS_INFO_CLASS.WTSUserName
    // SECURITY_LOGON_TYPE
    private const uint Interactive = 2, RemoteInteractive = 10, CachedInteractive = 11, CachedRemoteInteractive = 12;

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformation(IntPtr server, uint sessionId, int infoClass, out IntPtr buffer, out uint bytes);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    [DllImport("secur32.dll")]
    private static extern int LsaEnumerateLogonSessions(out uint count, out IntPtr luids);

    [DllImport("secur32.dll")]
    private static extern int LsaGetLogonSessionData(IntPtr luid, out IntPtr data);

    [DllImport("secur32.dll")]
    private static extern int LsaFreeReturnBuffer(IntPtr buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct LsaUnicodeString { public ushort Length; public ushort MaximumLength; public IntPtr Buffer; }

    /// <summary>The start of SECURITY_LOGON_SESSION_DATA: every Windows since XP has at least these.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct LogonSessionData
    {
        public uint Size;
        public Luid LogonId;
        public LsaUnicodeString UserName;
        public LsaUnicodeString LogonDomain;
        public LsaUnicodeString AuthenticationPackage;
        public uint LogonType;
        public uint Session;
        public IntPtr Sid;
    }
}
