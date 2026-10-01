using System.IO.Compression;
using System.Security.Cryptography;
using RemSound.Core;

namespace RemSound.App;

internal static partial class SelfTest
{
    /// <summary>THE UPDATER, END TO END (2026-09-28, a gap from the review of every step). Finding a release, downloading
    /// it, checking its signature, staging it and swapping it in were each tested, never as one chain - so a hand-over
    /// between two of them (the installer launched with the wrong folder, a zip wrapped in a folder not unwrapped) could
    /// break every update with every step green. Here the whole chain runs against a release site on this computer: a
    /// signed release is found, fetched, verified with a throwaway key, staged, and the installer the updater would launch
    /// is run in this process on a throwaway install. Then the same release, one byte changed in transit, is refused and
    /// the install left alone.</summary>
    private static string? TheUpdaterEndToEnd()
    {
        var root = Path.Combine(Path.GetTempPath(), "remsound-update-chain-" + Guid.NewGuid().ToString("N"));
        var install = Path.Combine(root, "install");
        Directory.CreateDirectory(install);
        void OldInstall()
        {
            File.WriteAllText(Path.Combine(install, "RemSound.exe"), "the old RemSound");
            File.WriteAllText(Path.Combine(install, "a.dll"), "the old a.dll");
            File.WriteAllText(Path.Combine(install, "yours.txt"), "not RemSound's");
            foreach (var marker in new[] { UpdateApplier.FailureMarkerName, RemSoundUpdater.WhatsNewMarkerName })
                try { File.Delete(Path.Combine(install, marker)); } catch { /* not there */ }
            try { File.Delete(Path.Combine(install, "b.dll")); } catch { /* not there */ }
        }
        OldInstall();

        // The release, wrapped in one folder as GitHub's zips are, and signed.
        const string tag = "v99.0.0";
        byte[] zip;
        using (var ms = new MemoryStream())
        {
            using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                void Add(string name, string text)
                {
                    using var w = new StreamWriter(archive.CreateEntry($"RemSound-{tag}/{name}").Open());
                    w.Write(text);
                }
                Add("RemSound.exe", "the new RemSound");
                Add("a.dll", "the new a.dll");
                Add("b.dll", "new in this release");
            }
            zip = ms.ToArray();
        }
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = key.ExportSubjectPublicKeyInfoPem();
        var signature = UpdateSignature.SignWithKey(zip, key.ExportECPrivateKeyPem());
        var served = zip;
        var asset = RemSoundUpdater.AssetNameTemplate.Replace("{tag}", tag);

        using var site = new StandInWebsite(path => path switch
        {
            "/zip" => ("application/zip", served),
            "/sig" => ("text/plain", System.Text.Encoding.ASCII.GetBytes(signature)),
            _ => null,
        });
        site.Json("/releases", $$"""
            [{"tag_name":"{{tag}}","body":"Notes","html_url":"{{site.Base}}/page","draft":false,"prerelease":false,
              "assets":[{"name":"{{asset}}","browser_download_url":"{{site.Base}}/zip"},
                        {"name":"{{asset}}{{UpdateSignature.SignatureAssetSuffix}}","browser_download_url":"{{site.Base}}/sig"}]}]
            """);

        var logged = new List<string>();
        System.Diagnostics.ProcessStartInfo? launched = null;
        RemSoundUpdater.ReleasesUrlForTest = site.Base + "/releases";
        RemSoundUpdater.InstallDirForTest = install;
        RemSoundUpdater.SignatureVerifiesForTest = (bytes, sig) => UpdateSignature.VerifyWithKey(bytes, sig, publicKey);
        RemSoundUpdater.LaunchForTest = psi => launched = psi;
        RemSoundUpdater.StageParentDirForTest = Path.Combine(root, "stage");
        try
        {
            var updater = new RemSoundUpdater { Log = line => { lock (logged) logged.Add(line); } };
            string Said() { lock (logged) return string.Join(" | ", logged.TakeLast(4)); }
            var found = updater.CheckForUpdateAsync().GetAwaiter().GetResult();
            var info = Require((found as UpdateAvailable)?.Info, $"the release on the stand-in site must be offered ({found}; {Said()})");
            Check(info.SignatureUrl is not null, "and its signature found beside it");

            Check(updater.DownloadAndStageInstallAsync(info).GetAwaiter().GetResult(), $"a signed release must be downloaded, verified and staged ({Said()})");
            var installer = Require(launched, "and the installer launched");
            Check(File.ReadAllText(installer.FileName) == "the new RemSound",
                $"THE HAND-OVER: the installer launched must be the NEW RemSound, unwrapped from its folder ({installer.FileName})");
            var args = installer.ArgumentList.ToList();
            var source = args[args.IndexOf("--update-source") + 1];
            Check(args[args.IndexOf("--update-target") + 1] == install, "and it must be told to update this install");
            var stage = args[args.IndexOf("--update-stage-root") + 1];
            // This process is the one it would wait for, and it will not exit: the wait is left out; nothing is restarted.
            var wait = args.IndexOf("--update-wait-pid");
            if (wait >= 0) args.RemoveRange(wait, 2);
            args.Add("--update-no-restart");
            UpdateApplier.Run([.. args]);

            Check(File.ReadAllText(Path.Combine(install, "RemSound.exe")) == "the new RemSound", "THE SWAP: the install must now hold the new RemSound");
            Check(File.ReadAllText(Path.Combine(install, "a.dll")) == "the new a.dll" && File.Exists(Path.Combine(install, "b.dll")),
                "and every file of the release, a new one included");
            Check(File.ReadAllText(Path.Combine(install, "yours.txt")) == "not RemSound's", "and leave a file that is not RemSound's alone");
            Check(!File.Exists(Path.Combine(install, UpdateApplier.FailureMarkerName)), "no failure noted");
            Check(WhatsNewMarker.Exists(install), "and the next start told to say what is new");
            Check(Directory.Exists(stage) && Directory.GetFiles(stage, "*.zip").Length == 0,
                "the downloaded zip must be deleted once installed (the staged copy it ran from stays until the next start)");
            RemSoundUpdater.CleanUpUpdateStages();   // what the next start does
            Check(!Directory.Exists(stage) && !Directory.Exists(source), $"and the next start must clear the staged copy away ({stage})");

            // The same release, one byte changed on the way: refused, nothing launched, the install as it was.
            OldInstall();
            launched = null;
            served = (byte[])zip.Clone();
            served[served.Length / 2] ^= 0x01;
            Check(!updater.DownloadAndStageInstallAsync(info).GetAwaiter().GetResult(), "THE TAMPERED DOWNLOAD: a release changed on the way must be refused");
            Check(launched is null, "and no installer launched");
            Check(File.ReadAllText(Path.Combine(install, "RemSound.exe")) == "the old RemSound", "and the install left exactly as it was");
            lock (logged) Check(logged.Any(l => l.Contains("REFUSED", StringComparison.Ordinal)), "and the refusal logged");
        }
        finally
        {
            RemSoundUpdater.ReleasesUrlForTest = null;
            RemSoundUpdater.InstallDirForTest = null;
            RemSoundUpdater.SignatureVerifiesForTest = null;
            RemSoundUpdater.LaunchForTest = null;
            RemSoundUpdater.StageParentDirForTest = null;
            try { Directory.Delete(root, recursive: true); } catch { /* temp */ }
        }
        return "a signed release on a site on this computer was found, downloaded, verified, unwrapped and staged; the installer "
             + "launched was the new RemSound, and it swapped every file in, left a file not RemSound's alone and deleted the zip, and the next start cleared the stage; "
             + "the same release with one byte changed was refused and the install left as it was";
    }

    /// <summary>A web site on this computer: a path either answers with a body, or is not found. Loopback only.</summary>
    private sealed class StandInWebsite : IDisposable
    {
        private readonly System.Net.Sockets.TcpListener listener = new(System.Net.IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        private readonly Func<string, (string Type, byte[] Body)?> route;
        private readonly Dictionary<string, string> json = [];
        public string Base => $"http://127.0.0.1:{((System.Net.IPEndPoint)listener.LocalEndpoint).Port}";

        public void Json(string path, string body) { lock (json) json[path] = body; }

        public StandInWebsite(Func<string, (string Type, byte[] Body)?> route)
        {
            this.route = route;
            listener.Start();
            _ = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    System.Net.Sockets.TcpClient client;
                    try { client = await listener.AcceptTcpClientAsync(stop.Token); }
                    catch { return; }
                    _ = Task.Run(() => Answer(client));
                }
            });
        }

        private async Task Answer(System.Net.Sockets.TcpClient client)
        {
            try
            {
                using (client)
                {
                    var s = client.GetStream();
                    var buf = new byte[4096];
                    var request = new System.Text.StringBuilder();
                    while (!request.ToString().Contains("\r\n\r\n"))
                    {
                        var n = await s.ReadAsync(buf, stop.Token);
                        if (n == 0) return;
                        request.Append(System.Text.Encoding.ASCII.GetString(buf, 0, n));
                    }
                    var path = request.ToString().Split(' ', 3) is [_, var p, _] ? p.Split('?')[0] : "/";
                    (string Type, byte[] Body)? answer;
                    lock (json) answer = json.TryGetValue(path, out var text) ? ("application/json", System.Text.Encoding.UTF8.GetBytes(text)) : null;
                    answer ??= route(path);
                    var head = answer is { } a
                        ? $"HTTP/1.1 200 OK\r\nContent-Type: {a.Type}\r\nContent-Length: {a.Body.Length}\r\nConnection: close\r\n\r\n"
                        : "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                    await s.WriteAsync(System.Text.Encoding.ASCII.GetBytes(head), stop.Token);
                    if (answer is { } body) await s.WriteAsync(body.Body, stop.Token);
                }
            }
            catch { /* the test is over */ }
        }

        public void Dispose() { stop.Cancel(); try { listener.Stop(); } catch { /* stopped */ } }
    }
}
