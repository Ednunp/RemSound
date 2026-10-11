using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace RemSound.App;

/// <summary>
/// The Web listener window's view of Tailscale Funnel (Ed, 2026-10-10: show this computer's address with a copy button,
/// and a quick way to Funnel). Everything goes through Tailscale's own command line, run only when the window opens and
/// when its button is pressed - never on a timer, so nothing keeps starting programs.
/// </summary>
internal static class TailscaleFunnel
{
    /// <summary>What Funnel looks like for the listener's port. <see cref="Address"/> is where a browser goes: when
    /// <see cref="On"/> is false it is where the page WOULD be, so the window can say so.</summary>
    internal sealed record State(bool Installed, string? Address, bool On, string Note);

    internal const string DownloadPage = "https://tailscale.com/download";

    public static string? FindExe()
    {
        foreach (var dir in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
        {
            if (string.IsNullOrEmpty(dir)) continue;
            var path = Path.Combine(dir, "Tailscale", "tailscale.exe");
            if (File.Exists(path)) return path;
        }
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try { var path = Path.Combine(dir.Trim(), "tailscale.exe"); if (File.Exists(path)) return path; } catch { /* a bad PATH entry */ }
        }
        return null;
    }

    /// <summary>Is Funnel passing the internet to <paramref name="port"/> on this computer, and at what address?</summary>
    public static async Task<State> QueryAsync(int port, CancellationToken ct)
    {
        var exe = FindExe();
        if (exe is null) return new State(false, null, false, "Tailscale isn't installed on this computer.");
        var (code, output) = await RunAsync(exe, "funnel status --json", null, TimeSpan.FromSeconds(10), ct);
        if (code == 0 && TryFindFunnel(output, port, out var on, out var host) && on) return new State(true, "https://" + host, true, "Tailscale Funnel is on.");
        var (statusCode, status) = await RunAsync(exe, "status --json", null, TimeSpan.FromSeconds(10), ct);
        string? name = null;
        var running = false;
        if (statusCode == 0)
        {
            try
            {
                using var doc = JsonDocument.Parse(status);
                running = doc.RootElement.TryGetProperty("BackendState", out var b) && b.GetString() == "Running";
                if (doc.RootElement.TryGetProperty("Self", out var self) && self.TryGetProperty("DNSName", out var dns)) name = dns.GetString()?.TrimEnd('.');
            }
            catch { /* treated as not known */ }
        }
        if (!running) return new State(true, null, false, "Tailscale is installed but not connected. Sign in to Tailscale first.");
        return new State(true, string.IsNullOrEmpty(name) ? null : "https://" + name, false, "Tailscale Funnel is off for RemSound's page.");
    }

    /// <summary>From <c>tailscale funnel status --json</c>: a host whose "/" goes to 127.0.0.1:port, and whether Funnel
    /// lets the internet in to it.</summary>
    internal static bool TryFindFunnel(string json, int port, out bool on, out string? host)
    {
        on = false;
        host = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("Web", out var web)) return false;
            foreach (var site in web.EnumerateObject())
            {
                if (!site.Value.TryGetProperty("Handlers", out var handlers) || !handlers.TryGetProperty("/", out var root)
                    || !root.TryGetProperty("Proxy", out var proxy)) continue;
                var target = proxy.GetString() ?? "";
                if (!target.EndsWith(":" + port, StringComparison.Ordinal) && !target.EndsWith(":" + port + "/", StringComparison.Ordinal)) continue;
                host = site.Name.EndsWith(":443", StringComparison.Ordinal) ? site.Name[..^4] : site.Name;
                on = doc.RootElement.TryGetProperty("AllowFunnel", out var allow) && allow.TryGetProperty(site.Name, out var yes)
                     && yes.ValueKind == JsonValueKind.True;
                return true;
            }
        }
        catch { /* not Funnel's answer */ }
        return false;
    }

    /// <summary>Turn Funnel on for <paramref name="port"/> (<c>tailscale funnel --bg port</c>). The first time, Tailscale
    /// answers with a page to approve and waits; on an account that has never used them it can ask for two in turn, web
    /// certificates (HTTPS) and then Funnel. Each page is handed to <paramref name="openPage"/> once. True when Tailscale
    /// says it's running.</summary>
    public static async Task<(bool Ok, string Message)> TurnOnAsync(int port, Action<string> openPage, CancellationToken ct)
    {
        var exe = FindExe();
        if (exe is null) return (false, "Tailscale isn't installed on this computer.");
        var opened = new HashSet<string>(StringComparer.Ordinal);
        var (code, output) = await RunAsync(exe, $"funnel --bg {port}", line =>
        {
            var at = line.IndexOf("https://login.tailscale.com/", StringComparison.Ordinal);
            if (at < 0) return;
            var page = line[at..].Trim();
            lock (opened) if (!opened.Add(page)) return;
            openPage(page);
        }, TimeSpan.FromMinutes(10), ct);
        if (code == 0) return (true, "Tailscale Funnel is on.");
        var last = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";
        return (false, "Tailscale Funnel could not be turned on" + (last.Length > 0 ? ": " + last : "."));
    }

    private static async Task<(int Code, string Output)> RunAsync(string exe, string arguments, Action<string>? onLine, TimeSpan timeout, CancellationToken ct)
    {
        var info = new ProcessStartInfo(exe, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        using var process = new Process { StartInfo = info };
        var output = new StringBuilder();
        void Take(string? line)
        {
            if (line is null) return;
            lock (output) output.AppendLine(line);
            try { onLine?.Invoke(line); } catch { /* the caller's problem, not the command's */ }
        }
        process.OutputDataReceived += (_, e) => Take(e.Data);
        process.ErrorDataReceived += (_, e) => Take(e.Data);
        try { process.Start(); }
        catch (Exception ex) { return (-1, ex.Message); }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try { await process.WaitForExitAsync(limit.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return (-1, "Tailscale did not answer in time.");
        }
        lock (output) return (process.ExitCode, output.ToString());
    }
}
