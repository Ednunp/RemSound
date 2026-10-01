using System.Runtime.InteropServices;
using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// THE PUSH TO TALK KEY, pressed and released (Ed, 2026-09-28). The other shortcuts use RegisterHotKey, which reports a
/// press and nothing else; hold to speak needs the release too, so this is a low-level keyboard hook. While push to talk is
/// on by key, RemSound takes the key for itself - press, repeats and release - so Windows never toggles Scroll Lock and
/// NVDA never announces it (Ed agreed). Off, the hook is gone and the key is Windows' again.
///
/// <para>It is installed only in a copy somebody is using: never in a --headless copy or the self-test, where it would
/// take the key from the person at the computer. <see cref="Handle"/> is the whole decision, so the gate proves it without
/// a hook.</para>
/// </summary>
internal sealed class PushToTalkKeyHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;

    private readonly HookProc proc;
    private readonly Action<bool, long> onKey;
    private IntPtr hook;
    private HotkeyInfo key = HotkeyInfo.Unset;
    private bool held;

    /// <param name="onKey">The key went down (true) or up (false), with the time in milliseconds.</param>
    public PushToTalkKeyHook(Action<bool, long> onKey)
    {
        this.onKey = onKey;
        proc = Callback;
    }

    /// <summary>While the window for choosing a key is open, the key must reach it - or Scroll Lock could never be
    /// chosen again while it is the push to talk key.</summary>
    public bool Paused { get; set; }

    public bool Installed => hook != IntPtr.Zero;

    public HotkeyInfo Key => key;

    /// <summary>Gate seam: the key, set without installing a hook - so <see cref="Handle"/> can be proved on its own.</summary>
    internal void UseKeyForTest(HotkeyInfo value) => key = value;

    /// <summary>Watch <paramref name="value"/>; an unset key removes the hook.</summary>
    public void Arm(HotkeyInfo value)
    {
        if (held && value != key) LetGo();
        key = value;
        if (key.IsUnset) { Disarm(); return; }
        if (hook == IntPtr.Zero) hook = SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(null), 0);
    }

    /// <summary>Stop watching; a key held at the time counts as let go.</summary>
    public void Disarm()
    {
        if (hook != IntPtr.Zero) { UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
        if (held) LetGo();
    }

    private void LetGo()
    {
        held = false;
        onKey(false, Environment.TickCount64);
    }

    /// <summary>
    /// One keyboard event: is it the push to talk key, and is it taken? A press counts when the key and its modifiers
    /// match; its repeats and its release are taken with it, whatever the modifiers do meanwhile. Anything else is left
    /// for Windows.
    /// </summary>
    internal bool Handle(int message, int virtualKey, bool control, bool shift, bool alt, long ms)
    {
        if (Paused || HotkeyCaptureForm.AnyOpen || key.IsUnset || virtualKey != (int)key.Key) return false;
        if (message is WM_KEYDOWN or WM_SYSKEYDOWN)
        {
            if (!held)
            {
                if (control != key.Control || shift != key.Shift || alt != key.Alt) return false;
                held = true;
                onKey(true, ms);
            }
            return true;
        }
        if (message is not (WM_KEYUP or WM_SYSKEYUP) || !held) return false;
        held = false;
        onKey(false, ms);
        return true;
    }

    private IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (code >= 0)
            {
                var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
                if (Handle((int)wParam, (int)info.vkCode, Down(VK_CONTROL), Down(VK_SHIFT), Down(VK_MENU), Environment.TickCount64))
                    return 1;
            }
        }
        catch { /* never let the keyboard stall on us */ }
        return CallNextHookEx(hook, code, wParam, lParam);
    }

    public void Dispose() => Disarm();

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
