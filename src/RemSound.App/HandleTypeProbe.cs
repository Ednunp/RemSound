using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace RemSound.App;

/// <summary>
/// Counts THIS process's open OS handles grouped by type (Event, Section, File, Key, Thread, …).
/// Added 2026-06-07 to pin down the receiver handle leak: the diag line's <c>handles=</c> column
/// proved the leak is OS handles, but not WHICH kind — and the kind names the culprit (Event ⇒
/// a waitable-object leak, Section ⇒ a WASAPI buffer/audio-client leak, Key ⇒ a CNG/AesGcm leak,
/// Thread ⇒ thread-handle leak, etc.).
///
/// Primary mechanism: NtQueryInformationProcess(ProcessHandleInformation) returns ONLY the calling
/// process's handle table — small, fast, and it never touches the system-wide handle table. The
/// original system-wide walk via NtQuerySystemInformation(SystemExtendedHandleInformation) returned
/// STATUS_ACCESS_VIOLATION (0xC0000005) on Andre's ASUS-Realtek machine, so it is now only a
/// fallback for any box where the per-process class is unavailable. Type names are resolved per
/// distinct ObjectTypeIndex ONCE via NtQueryObject(ObjectTypeInformation) on a sample handle —
/// class 2 is safe on our own handles (the known NtQueryObject hang only affects ObjectName-
/// Information, class 1, on synchronous pipes, which we never request). Everything is wrapped in
/// try/catch and unmanaged buffers are always freed, so a failure degrades to a probe-error string
/// rather than disturbing the very memory we're measuring. x64 only (the shipped runtime).
/// </summary>
internal static class HandleTypeProbe
{
    private const int ProcessHandleInformation = 51;          // PROCESSINFOCLASS
    private const int SystemExtendedHandleInformation = 0x40; // SYSTEM_INFORMATION_CLASS
    private const int ObjectTypeInformation = 2;
    private const uint STATUS_INFO_LENGTH_MISMATCH = 0xC0000004;

    // PROCESS_HANDLE_SNAPSHOT_INFORMATION (x64): NumberOfHandles (ULONG_PTR) +0, Reserved +8,
    // then PROCESS_HANDLE_TABLE_ENTRY_INFO[] at +16. Each entry is 40 bytes:
    //   HandleValue +0, HandleCount +8, PointerCount +16, GrantedAccess +24,
    //   ObjectTypeIndex (ULONG) +28, HandleAttributes +32, Reserved +36.
    private const int PhHeaderSize = 16;
    private const int PhEntrySize = 40;
    private const int PhOffHandleValue = 0;
    private const int PhOffObjectTypeIndex = 28;

    // 64-bit SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX is 40 bytes; UniqueProcessId at +8, HandleValue at
    // +16, ObjectTypeIndex (USHORT) at +30. Header is 16 bytes (NumberOfHandles + Reserved).
    private const int SysHeaderSize = 16;
    private const int SysEntrySize = 40;
    private const int SysOffUniqueProcessId = 8;
    private const int SysOffHandleValue = 16;
    private const int SysOffObjectTypeIndex = 30;

    // Cap any snapshot so a pathological system can't make us allocate without bound (we're
    // hunting a leak — don't become one). 128 MB covers well over a million handles.
    private const int MaxBufferBytes = 128 * 1024 * 1024;

    private static readonly nint CurrentProcessPseudoHandle = (nint)(-1);
    private static readonly Dictionary<ushort, string> TypeNameByIndex = new();
    private static readonly int OwnPid = Process.GetCurrentProcess().Id;

    /// <summary>
    /// Returns "Event=120345 Section=15234 File=210 … total=N" — the <paramref name="topN"/> most
    /// common handle types owned by this process. Tries the per-process query first, falls back to
    /// the system-wide walk, and returns a probe-error string (never throws) if both fail.
    /// Heavier than the per-tick meter, so call it on a slow cadence, not every diag line.
    /// </summary>
    public static string Summarize(int topN = 10)
    {
        var own = TryOwnProcess(topN, out var ownStatus);
        if (own != null) return own;
        var sys = TrySystemWide(topN, out var sysStatus);
        if (sys != null) return sys;
        return $"probe-error proc-status=0x{ownStatus:X8} sys-status=0x{sysStatus:X8}";
    }

    /// <summary>
    /// Primary path: query only THIS process's handle table. Returns the formatted summary, or null
    /// on any failure (with <paramref name="status"/> set to the NTSTATUS for diagnostics).
    /// </summary>
    private static string? TryOwnProcess(int topN, out uint status)
    {
        nint buffer = 0;
        status = 0;
        try
        {
            var size = 1 << 18; // 256 KB — our own table is small even when leaking.
            while (true)
            {
                buffer = buffer == 0 ? Marshal.AllocHGlobal(size) : Marshal.ReAllocHGlobal(buffer, (nint)size);
                status = NtQueryInformationProcess(CurrentProcessPseudoHandle, ProcessHandleInformation, buffer, (uint)size, out var needed);
                if (status != STATUS_INFO_LENGTH_MISMATCH) break;
                size = (int)System.Math.Min((long)System.Math.Max(needed, (uint)size) * 2, MaxBufferBytes);
                if (size >= MaxBufferBytes) { status = NtQueryInformationProcess(CurrentProcessPseudoHandle, ProcessHandleInformation, buffer, (uint)size, out _); break; }
            }
            if (status != 0) return null;

            var count = Marshal.ReadInt64(buffer); // NumberOfHandles
            var counts = new Dictionary<ushort, int>();
            var entryBase = buffer + PhHeaderSize;
            for (long i = 0; i < count; i++)
            {
                var entry = entryBase + (nint)(i * PhEntrySize);
                var typeIndex = (ushort)Marshal.ReadInt32(entry + PhOffObjectTypeIndex);
                counts.TryGetValue(typeIndex, out var c);
                counts[typeIndex] = c + 1;
                if (!TypeNameByIndex.ContainsKey(typeIndex))
                {
                    var handle = Marshal.ReadIntPtr(entry + PhOffHandleValue);
                    TypeNameByIndex[typeIndex] = ResolveTypeName(handle, typeIndex);
                }
            }
            return counts.Count == 0 ? null : Format(counts, topN);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (buffer != 0) Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Fallback path: walk the whole system handle table and filter to our PID. Returns null on any
    /// failure (with <paramref name="status"/> set). Kept for machines where the per-process class
    /// is unavailable; on Andre's box this path returns STATUS_ACCESS_VIOLATION, which is exactly
    /// why <see cref="TryOwnProcess"/> is tried first.
    /// </summary>
    private static string? TrySystemWide(int topN, out uint status)
    {
        nint buffer = 0;
        status = 0;
        try
        {
            var size = 1 << 20; // 1 MB to start; grow on mismatch.
            while (true)
            {
                buffer = buffer == 0 ? Marshal.AllocHGlobal(size) : Marshal.ReAllocHGlobal(buffer, (nint)size);
                status = NtQuerySystemInformation(SystemExtendedHandleInformation, buffer, (uint)size, out var needed);
                if (status != STATUS_INFO_LENGTH_MISMATCH) break;
                size = (int)System.Math.Min((long)System.Math.Max(needed, (uint)size) * 2, MaxBufferBytes);
                if (size >= MaxBufferBytes) { status = NtQuerySystemInformation(SystemExtendedHandleInformation, buffer, (uint)size, out _); break; }
            }
            if (status != 0) return null;

            var count = Marshal.ReadInt64(buffer); // NumberOfHandles
            var counts = new Dictionary<ushort, int>();
            var entryBase = buffer + SysHeaderSize;
            for (long i = 0; i < count; i++)
            {
                var entry = entryBase + (nint)(i * SysEntrySize);
                var pid = (int)Marshal.ReadInt64(entry + SysOffUniqueProcessId);
                if (pid != OwnPid) continue;
                var typeIndex = (ushort)Marshal.ReadInt16(entry + SysOffObjectTypeIndex);
                counts.TryGetValue(typeIndex, out var c);
                counts[typeIndex] = c + 1;
                if (!TypeNameByIndex.ContainsKey(typeIndex))
                {
                    var handle = Marshal.ReadIntPtr(entry + SysOffHandleValue);
                    TypeNameByIndex[typeIndex] = ResolveTypeName(handle, typeIndex);
                }
            }
            return counts.Count == 0 ? null : Format(counts, topN);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (buffer != 0) Marshal.FreeHGlobal(buffer);
        }
    }

    private static string Format(Dictionary<ushort, int> counts, int topN)
    {
        var ordered = new List<KeyValuePair<ushort, int>>(counts);
        ordered.Sort((a, b) => b.Value.CompareTo(a.Value));

        var sb = new StringBuilder();
        var total = 0;
        var shown = 0;
        foreach (var kv in ordered)
        {
            total += kv.Value;
            if (shown < topN)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(TypeNameByIndex.TryGetValue(kv.Key, out var n) ? n : $"Type#{kv.Key}").Append('=').Append(kv.Value);
                shown++;
            }
        }
        sb.Append(" total=").Append(total);
        return sb.ToString();
    }

    private static string ResolveTypeName(nint handle, ushort index)
    {
        nint info = 0;
        try
        {
            const int len = 4096;
            info = Marshal.AllocHGlobal(len);
            var status = NtQueryObject(handle, ObjectTypeInformation, info, len, out _);
            if (status != 0) return $"Type#{index}";
            // OBJECT_TYPE_INFORMATION starts with UNICODE_STRING TypeName { USHORT Length;
            // USHORT MaximumLength; PWSTR Buffer; } — Length at +0, Buffer (ptr) at +8 on x64.
            var nameLen = (ushort)Marshal.ReadInt16(info);
            var namePtr = Marshal.ReadIntPtr(info + 8);
            if (namePtr == 0 || nameLen == 0) return $"Type#{index}";
            var name = Marshal.PtrToStringUni(namePtr, nameLen / 2);
            return string.IsNullOrEmpty(name) ? $"Type#{index}" : name;
        }
        catch
        {
            return $"Type#{index}";
        }
        finally
        {
            if (info != 0) Marshal.FreeHGlobal(info);
        }
    }

    [DllImport("ntdll.dll")]
    private static extern uint NtQueryInformationProcess(nint processHandle, int processInformationClass, nint processInformation, uint processInformationLength, out uint returnLength);

    [DllImport("ntdll.dll")]
    private static extern uint NtQuerySystemInformation(int systemInformationClass, nint systemInformation, uint systemInformationLength, out uint returnLength);

    [DllImport("ntdll.dll")]
    private static extern uint NtQueryObject(nint handle, int objectInformationClass, nint objectInformation, int objectInformationLength, out int returnLength);
}
