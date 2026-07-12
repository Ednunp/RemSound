using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace RemSound.Sender;

/// <summary>One application that currently has an audio session, for the "send specific applications"
/// picker. <see cref="ProcessName"/> is the stable identity (lower-case, no path/extension), tracked
/// across restarts; <see cref="Pid"/> is the current process id used to open a process-loopback capture
/// and is transient. <see cref="Playing"/> is true when at least one of the app's sessions is active
/// (actually producing sound right now) rather than merely open.</summary>
public sealed record AudioApp(string ProcessName, string DisplayName, int Pid, bool Playing);

/// <summary>
/// Enumerates the applications that currently have audio sessions on the machine's render devices, for
/// the per-application send picker. Snapshot-only: every call re-enumerates from scratch and RELEASES
/// the NAudio session objects immediately — holding them is what piles up (the OS never expires a
/// session while a reference is alive). Best-effort throughout; a device that can't be read is skipped.
/// </summary>
public static class AudioAppEnumerator
{
    /// <summary>Current audio apps, one entry per process (merged across sessions and devices), sorted by
    /// display name. Never throws.</summary>
    public static IReadOnlyList<AudioApp> Snapshot()
    {
        // Merge by process name: an app can have several sessions (and PIDs); it "plays" if any is active.
        var byName = new Dictionary<string, (string Display, int Pid, bool Playing)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var en = new MMDeviceEnumerator();
            foreach (var device in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                try
                {
                    var mgr = device.AudioSessionManager; // realises the session manager for this device
                    mgr.RefreshSessions();
                    var sessions = mgr.Sessions;
                    for (var i = 0; i < sessions.Count; i++)
                    {
                        var session = sessions[i];
                        try
                        {
                            if (session.IsSystemSoundsSession) continue;
                            var pid = (int)session.GetProcessID;
                            if (pid <= 0) continue;
                            var playing = session.State == AudioSessionState.AudioSessionStateActive;
                            var (name, display) = ResolveProcess(pid);
                            if (name.Length == 0) continue;
                            if (byName.TryGetValue(name, out var existing))
                            {
                                // Prefer a playing PID as the representative; keep any "playing" flag.
                                byName[name] = (existing.Display, playing && !existing.Playing ? pid : existing.Pid, existing.Playing || playing);
                            }
                            else
                            {
                                byName[name] = (display, pid, playing);
                            }
                        }
                        catch { /* skip a session we can't read */ }
                        // NOTE: do NOT retain `session`; NAudio's AudioSessionControl holds a COM ref and
                        // the OS never expires it while referenced. We've taken what we need; drop it.
                    }
                }
                catch { /* device without a usable session manager — skip */ }
                finally { try { device.Dispose(); } catch { } }
            }
        }
        catch { /* enumerator failure — return whatever we gathered */ }

        return byName
            .Select(kv => new AudioApp(kv.Key, kv.Value.Display, kv.Value.Pid, kv.Value.Playing))
            .OrderBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>The current PIDs for a process NAME (an app may run several processes). Used at capture
    /// time to resolve a name-based selection to the live processes to loopback-capture.</summary>
    public static IReadOnlyList<int> PidsForProcessName(string processName)
    {
        try
        {
            return Process.GetProcessesByName(processName).Select(p =>
            {
                var id = 0;
                try { id = p.Id; } catch { }
                finally { try { p.Dispose(); } catch { } }
                return id;
            }).Where(id => id > 0).ToList();
        }
        catch { return Array.Empty<int>(); }
    }

    // (name, friendly display) for a PID. Name = lower-case ProcessName (no .exe) — the stable identity.
    // Display = the exe's FileDescription when readable (e.g. "VLC media player"), else the process name.
    private static (string Name, string Display) ResolveProcess(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            var name = p.ProcessName; // already no ".exe"
            if (string.IsNullOrWhiteSpace(name)) return ("", "");
            var display = name;
            try
            {
                var desc = p.MainModule?.FileVersionInfo.FileDescription;
                if (!string.IsNullOrWhiteSpace(desc)) display = desc!;
            }
            catch { /* MainModule can throw (access denied / 32-vs-64) — fall back to the name */ }
            return (name.ToLowerInvariant(), display);
        }
        catch { return ("", ""); }
    }
}
