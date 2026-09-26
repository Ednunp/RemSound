using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Windows.Forms;
using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// The app's updater when things go wrong (review 2026-09-25; Ed agreed the fixes: "make sure you test that as it sounds
/// like there could be lots of things that go wrong"). A rollback tried each file once and, whatever happened, deleted the
/// backup and wrote that the old version was back "exactly as it was"; a download that stalled with its connection open
/// was waited on for ever, and every later "Install now" did nothing until RemSound restarted. Everything here uses real
/// files, real locks and a real HTTP server on this machine - never the internet, never the real install.
/// </summary>
internal static partial class SelfTest
{
    /// <summary>
    /// A FAILED UPDATE KEEPS WHAT IT COULD NOT PUT BACK, AND SAYS SO.
    ///
    /// <para>Driven through UpdateApplier.Run, the installer RemSound really runs, on a throwaway install: an update that
    /// works; one that fails and is put back exactly; one whose rollback only works because it now keeps trying; one whose
    /// rollback cannot finish; the next update after that; and an update that finds an old backup lying there. And what
    /// the service and the next start make of each.</para>
    /// </summary>
    private static string? AFailedUpdateKeepsWhatItCouldNotPutBack()
    {
        var restoreAttempts = UpdateApplier.CopyRetryAttempts;
        var restoreDelay = UpdateApplier.CopyRetryDelayMs;
        UpdateApplier.CopyRetryAttempts = 10;
        UpdateApplier.CopyRetryDelayMs = 100;   // a lock costs about a second per file, not a minute
        var root = Path.Combine(Path.GetTempPath(), "remsound-updatefail-" + Guid.NewGuid().ToString("N"));
        var locks = new List<FileStream>();
        try
        {
            var release = Path.Combine(root, "release");
            Directory.CreateDirectory(release);
            File.WriteAllText(Path.Combine(release, "a.dll"), "new-a");
            File.WriteAllText(Path.Combine(release, "b.dll"), "new-b");
            File.WriteAllText(Path.Combine(release, "n.dll"), "new-n");   // new in this release

            string Install(string name)
            {
                var dir = Path.Combine(root, name);
                Directory.CreateDirectory(Path.Combine(dir, "user settings and logs"));
                File.WriteAllText(Path.Combine(dir, "a.dll"), "old-a");
                File.WriteAllText(Path.Combine(dir, "b.dll"), "old-b");
                File.WriteAllText(Path.Combine(dir, "user settings and logs", "config.json"), "mine");
                return dir;
            }
            void Apply(string install) => UpdateApplier.Run(["--apply-update", "--update-source", release, "--update-target", install, "--update-no-restart"]);
            string Read(string install, string file) => File.Exists(Path.Combine(install, file)) ? File.ReadAllText(Path.Combine(install, file)) : "(missing)";
            string UpdaterLog(string install) => File.Exists(Path.Combine(install, "updater.log")) ? File.ReadAllText(Path.Combine(install, "updater.log")) : "";
            string[] Kept(string install) => Directory.GetDirectories(install, UpdateApplier.KeptBackupPrefix + "*");
            FileStream Lock(string path) { var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); locks.Add(f); return f; }
            bool Marker(string install, string name) => File.Exists(Path.Combine(install, name));

            // 1. It works: everything new, nothing left behind, and a note left by an earlier broken update is gone.
            var ok = Install("ok");
            File.WriteAllText(Path.Combine(ok, UpdateApplier.IncompleteMarkerName), "from before");
            Apply(ok);
            Check(Read(ok, "a.dll") == "new-a" && Read(ok, "b.dll") == "new-b" && Read(ok, "n.dll") == "new-n",
                "premise: an update with nothing in the way puts every new file in");
            Check(!Directory.Exists(Path.Combine(ok, UpdateApplier.BackupFolderName)) && Kept(ok).Length == 0, "and leaves no backup behind");
            Check(!Marker(ok, UpdateApplier.IncompleteMarkerName) && !Marker(ok, UpdateApplier.FailureMarkerName),
                "and an install an earlier update left broken is whole again, so its note goes");
            Check(!ServiceUpdate.SwapInProgressIn(ok), "and the service may update from it");
            Check(Read(ok, Path.Combine("user settings and logs", "config.json")) == "mine", "and the person's own settings are untouched");

            // 2. It fails (b.dll is locked for good) and is put back exactly.
            var back = Install("back");
            Lock(Path.Combine(back, "b.dll"));
            Apply(back);
            Check(Read(back, "a.dll") == "old-a" && Read(back, "b.dll") == "old-b" && Read(back, "n.dll") == "(missing)",
                $"a failed update that could be put back must leave the old version exactly (a={Read(back, "a.dll")}, n={Read(back, "n.dll")})");
            Check(Marker(back, UpdateApplier.FailureMarkerName) && !Marker(back, UpdateApplier.IncompleteMarkerName),
                "and say it failed and was put back, not that anything is wrong");
            Check(!Directory.Exists(Path.Combine(back, UpdateApplier.BackupFolderName)) && Kept(back).Length == 0, "with no backup left behind");
            Check(!ServiceUpdate.SwapInProgressIn(back), "and the service may carry on updating from it");
            var told = MainForm.UpdateNoteToTell(back);
            Check(told is { Once: true } t1 && t1.Text.Contains("exactly as it was", StringComparison.Ordinal),
                "the next start must say, once, that the update failed and the old version is back");

            // 3. The rollback works only because it now keeps trying: the new a.dll is held for a moment - the same kind of
            //    lock that failed the swap - and let go while the rollback is still at it. Once, it tried once and gave up.
            var retried = Install("retried");
            Lock(Path.Combine(retried, "b.dll"));
            FileStream? briefly = null;
            UpdateApplier.AfterCopyForTest = path =>
            {
                if (!path.EndsWith("a.dll", StringComparison.OrdinalIgnoreCase)) return;
                briefly = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                var held = briefly;
                // The swap spends about a second failing on b.dll; let a.dll go half way through the rollback's own second.
                _ = Task.Delay(1500).ContinueWith(_ => held.Dispose());
            };
            Apply(retried);
            UpdateApplier.AfterCopyForTest = null;
            briefly?.Dispose();
            Check(Read(retried, "a.dll") == "old-a" && !Marker(retried, UpdateApplier.IncompleteMarkerName),
                $"THE ONE TRY: a file held for a moment during the rollback must still be put back (a={Read(retried, "a.dll")})");

            // 4. The rollback cannot finish: the new a.dll stays locked. The backup used to be deleted anyway.
            var stuck = Install("stuck");
            Lock(Path.Combine(stuck, "b.dll"));
            UpdateApplier.AfterCopyForTest = path => { if (path.EndsWith("a.dll", StringComparison.OrdinalIgnoreCase)) Lock(path); };
            Apply(stuck);
            UpdateApplier.AfterCopyForTest = null;
            var kept = Kept(stuck);
            Check(kept.Length == 1 && File.Exists(Path.Combine(kept[0], "a.dll")) && File.ReadAllText(Path.Combine(kept[0], "a.dll")) == "old-a",
                $"THE LOST BACKUP: the old a.dll it could not put back must be kept, not deleted ({kept.Length} kept folders)");
            Check(!Directory.Exists(Path.Combine(stuck, UpdateApplier.BackupFolderName)),
                "kept under its own name, so the service is not left waiting for ever on a swap that has ended");
            Check(!Marker(stuck, UpdateApplier.FailureMarkerName),
                "THE FALSE COMFORT: it must not say the old version is back exactly as it was");
            var note = Read(stuck, UpdateApplier.IncompleteMarkerName);
            Check(note.Contains("a.dll", StringComparison.Ordinal) && note.Contains(kept.FirstOrDefault() ?? "\0", StringComparison.Ordinal),
                $"and must say which file is wrong and where the old one is kept (note: {Head(note)})");
            Check(UpdaterLog(stuck).Contains("ROLLBACK INCOMPLETE", StringComparison.Ordinal), "and its log must say so");
            Check(MainForm.UpdateNoteToTell(stuck) is { Once: false }, "and every start must say so until it is put right");
            Check(ServiceUpdate.SwapInProgressIn(stuck), "the service must not copy a half-put-back install");
            var service = Path.Combine(root, "service-bin");
            ServiceControl.CopyProgramTo(stuck, service);
            Check(!Directory.GetDirectories(service, UpdateApplier.KeptBackupPrefix + "*").Any(),
                "and the kept old files are never taken into the service as program files");
            Check(ServiceControl.IsSkippedProgramFolder(Path.GetFileName(kept[0])), "(nor counted as the program's)");

            // 5. Once nothing is locked, the next update puts it right - and still leaves the kept files alone.
            foreach (var l in locks) l.Dispose();
            locks.Clear();
            Apply(stuck);
            Check(Read(stuck, "a.dll") == "new-a" && Read(stuck, "b.dll") == "new-b", "the next update must go through");
            Check(!Marker(stuck, UpdateApplier.IncompleteMarkerName) && !ServiceUpdate.SwapInProgressIn(stuck),
                "and clear the note, so the service may update again");
            Check(Kept(stuck).Length == 1, "the old files kept before are not deleted by a later update");

            // 6. An update that finds a backup left from before - a swap that stopped half way - keeps it, never deletes it.
            var leftover = Install("leftover");
            Directory.CreateDirectory(Path.Combine(leftover, UpdateApplier.BackupFolderName));
            File.WriteAllText(Path.Combine(leftover, UpdateApplier.BackupFolderName, "a.dll"), "the only copy");
            Apply(leftover);
            var keptBefore = Kept(leftover);
            Check(keptBefore.Length == 1 && File.ReadAllText(Path.Combine(keptBefore[0], "a.dll")) == "the only copy",
                "a backup found lying there must be kept, not deleted without a look");
            Check(Read(leftover, "a.dll") == "new-a", "and the update still goes through");

            // What is read out at the next start, and the only-once rule, are pinned in the window's own code.
            var src = FindSourceRoot();
            if (src is null) return Skip("the updater keeps and says what it must; the source is not reachable to check the start-up message");
            var mainForm = File.ReadAllText(Path.Combine(src, "src", "RemSound.App", "MainForm.cs"));
            var startup = SourceMethodBody(mainForm, "private void RunStartupNotices(bool coldStart)");
            Check(startup.Contains("MaybeTellAboutFailedUpdate();", StringComparison.Ordinal), "the start-up notices must include the update note");
            var tell = SourceMethodBody(mainForm, "private void MaybeTellAboutFailedUpdate()");
            // The once-only note: said, then deleted, inside its own branch - and nobody-to-tell is asked before either.
            var once = tell.IndexOf("if (n.Once)", StringComparison.Ordinal);
            var shown = once < 0 ? -1 : tell.IndexOf("AppMessageBox.Show(", once, StringComparison.Ordinal);
            var deleted = once < 0 ? -1 : tell.IndexOf("File.Delete(Path.Combine(dir, UpdateApplier.FailureMarkerName))", once, StringComparison.Ordinal);
            Check(once > 0 && shown > once && deleted > shown
                  && tell.IndexOf("Windowless.NobodyToAsk", StringComparison.Ordinal) is > 0 and var ask && ask < once,
                "the once-only note must be deleted only after it has been said, and never by a copy with nobody to tell");
            return "an update that works leaves nothing behind; one that fails is put back exactly and says so once; a file held "
                 + "for a moment is still put back; one that cannot be put back keeps the old file, says which and where at every "
                 + "start, and stops the service copying; the next update puts it right; an old backup is never deleted";
        }
        finally
        {
            UpdateApplier.AfterCopyForTest = null;
            foreach (var l in locks) { try { l.Dispose(); } catch { /* released */ } }
            UpdateApplier.CopyRetryAttempts = restoreAttempts;
            UpdateApplier.CopyRetryDelayMs = restoreDelay;
            try { Directory.Delete(root, recursive: true); } catch { /* temp */ }
        }
    }

    /// <summary>A plain HTTP server on this machine that sends a "release" slowly, or stops half way and holds the
    /// connection open - what a stalled download looks like from the updater's side.</summary>
    private sealed class TrickleServer : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        public int Requests;

        public string Url => $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/RemSound-v99.0.zip";

        /// <param name="stallAfter">Bytes after which it goes silent and holds the connection open; -1 never.</param>
        public TrickleServer(int total, int chunk, TimeSpan every, int stallAfter)
        {
            listener.Start();
            _ = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    TcpClient client;
                    try { client = await listener.AcceptTcpClientAsync(stop.Token); }
                    catch { return; }
                    Interlocked.Increment(ref Requests);
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            using (client)
                            {
                                var s = client.GetStream();
                                var buf = new byte[4096];
                                var request = new StringBuilder();
                                while (!request.ToString().Contains("\r\n\r\n"))
                                {
                                    var n = await s.ReadAsync(buf, stop.Token);
                                    if (n == 0) return;
                                    request.Append(Encoding.ASCII.GetString(buf, 0, n));
                                }
                                await s.WriteAsync(Encoding.ASCII.GetBytes(
                                    $"HTTP/1.1 200 OK\r\nContent-Type: application/zip\r\nContent-Length: {total}\r\nConnection: close\r\n\r\n"), stop.Token);
                                var block = new byte[chunk];
                                for (var sent = 0; sent < total; )
                                {
                                    if (stallAfter >= 0 && sent >= stallAfter) await Task.Delay(Timeout.Infinite, stop.Token);
                                    var n = Math.Min(chunk, total - sent);
                                    await s.WriteAsync(block.AsMemory(0, n), stop.Token);
                                    sent += n;
                                    if (every > TimeSpan.Zero) await Task.Delay(every, stop.Token);
                                }
                            }
                        }
                        catch { /* the test is over */ }
                    });
                }
            });
        }

        public void Dispose() { stop.Cancel(); try { listener.Stop(); } catch { /* stopped */ } }
    }

    /// <summary>The stage folders a download test made, so they can be removed afterwards.</summary>
    private static HashSet<string> UpdateStages()
    {
        try { return new HashSet<string>(Directory.GetDirectories(RemSoundUpdater.UpdateStageParentDir), StringComparer.OrdinalIgnoreCase); }
        catch { return new HashSet<string>(StringComparer.OrdinalIgnoreCase); }
    }

    private static void RemoveNewStages(HashSet<string> before)
    {
        foreach (var dir in UpdateStages().Where(d => !before.Contains(d)))
            try { Directory.Delete(dir, recursive: true); } catch { /* temp */ }
    }

    /// <summary>
    /// A DOWNLOAD THAT STOPS ARRIVING IS GIVEN UP; A SLOW ONE THAT KEEPS ARRIVING IS NOT.
    ///
    /// <para>Through the real download (RemSoundUpdater.DownloadAndStageInstallAsync with its real HTTP client) from a real
    /// HTTP server on this machine: one that sends a little and then holds the connection open with nothing more, and one
    /// that sends slowly - longer in all than the limit, but never silent for as long as it.</para>
    /// </summary>
    private static string? AStalledDownloadIsGivenUpASlowOneIsNot()
    {
        var restoreLimit = RemSoundUpdater.DownloadStallLimit;
        var restoreDir = RemSoundUpdater.InstallDirForTest;
        var install = Path.Combine(Path.GetTempPath(), "remsound-stall-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(install);
        RemSoundUpdater.InstallDirForTest = install;
        RemSoundUpdater.DownloadStallLimit = TimeSpan.FromSeconds(1);
        var stagesBefore = UpdateStages();
        try
        {
            UpdateInfo Release(string url) => new("v99.0", new Version(99, 0, 0), url, "", "https://example.invalid/release");

            using (var stalling = new TrickleServer(total: 100_000, chunk: 1000, every: TimeSpan.Zero, stallAfter: 1000))
            {
                var lines = new ConcurrentQueue<string>();
                var updater = new RemSoundUpdater { Log = lines.Enqueue };
                var started = DateTime.UtcNow;
                var download = updater.DownloadAndStageInstallAsync(Release(stalling.Url));
                Check(download.Wait(TimeSpan.FromSeconds(20)),
                    "THE ENDLESS WAIT: a download that stops arriving with its connection open must be given up, not waited on for ever");
                Check(!download.Result, "and must report that it failed, so the person is told and can try again");
                Check(lines.Any(l => l.Contains("stalled", StringComparison.Ordinal)),
                    $"and the log must say it stalled (it said: {string.Join(" | ", lines.TakeLast(3))})");
                Check(DateTime.UtcNow - started < TimeSpan.FromSeconds(10), "within a few seconds of the limit");
            }

            using (var slow = new TrickleServer(total: 1300, chunk: 100, every: TimeSpan.FromMilliseconds(300), stallAfter: -1))
            {
                var lines = new ConcurrentQueue<string>();
                var updater = new RemSoundUpdater { Log = lines.Enqueue };
                var started = DateTime.UtcNow;
                var download = updater.DownloadAndStageInstallAsync(Release(slow.Url));
                Check(download.Wait(TimeSpan.FromSeconds(20)), "the slow download must finish");
                Check(!lines.Any(l => l.Contains("stalled", StringComparison.Ordinal)),
                    "a download that keeps arriving must never be cut off, however slow - it is silence that counts, not the total");
                Check(DateTime.UtcNow - started > TimeSpan.FromSeconds(2), "premise: the slow download took longer than the limit in all");
                Check(lines.Any(l => l.Contains("REFUSED", StringComparison.Ordinal) && l.Contains("no signature", StringComparison.Ordinal)),
                    $"and it must reach the signature check (this pretend release has none, so it is refused there) (it said: {string.Join(" | ", lines.TakeLast(3))})");
            }
            return "a download that stops arriving is given up at the limit and says it stalled; a slow one that keeps arriving, "
                 + "longer in all than the limit, is not";
        }
        finally
        {
            RemSoundUpdater.DownloadStallLimit = restoreLimit;
            RemSoundUpdater.InstallDirForTest = restoreDir;
            RemoveNewStages(stagesBefore);
            try { Directory.Delete(install, recursive: true); } catch { /* temp */ }
        }
    }

    /// <summary>
    /// AFTER A STALLED DOWNLOAD, INSTALL NOW WORKS AGAIN.
    ///
    /// <para>The real window's install, twice, against a download that stalls: each time the person is told it failed, and
    /// the second really downloads again - it used to be ignored as "already in progress" until RemSound restarted. The
    /// failure message is answered through the control channel, as in a headless copy.</para>
    /// </summary>
    private static string? InstallNowWorksAgainAfterAStalledDownload()
    {
        var restoreMuted = CuePlayer.GloballyMuted;
        CuePlayer.GloballyMuted = true;
        var restoreLimit = RemSoundUpdater.DownloadStallLimit;
        var restoreDir = RemSoundUpdater.InstallDirForTest;
        var install = Path.Combine(Path.GetTempPath(), "remsound-stall-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(install);
        RemSoundUpdater.InstallDirForTest = install;
        RemSoundUpdater.DownloadStallLimit = TimeSpan.FromSeconds(1);
        var stagesBefore = UpdateStages();
        Windowless.InstallHook();
        Windowless.SetActiveForTest(true);
        MainForm? form = null;
        try
        {
            var store = StoreWithOneProfileForTest();
            try { form = new MainForm(store, store.Load("Audit profile") ?? Profile.NewBlank(), "Audit profile", null, headless: true); }
            catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }
            _ = form.Handle;
            var main = form;
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var installNow = Require(typeof(MainForm).GetMethod("InstallUpdateAsync", flags, [typeof(UpdateInfo)]),
                "MainForm.InstallUpdateAsync(UpdateInfo) not found");
            var engine = new RemoteControlEngine(() => main);
            bool QuestionSays(string words) => main.Invoke(() => engine.Execute("windows")).Contains(words, StringComparison.Ordinal);

            using var stalling = new TrickleServer(total: 100_000, chunk: 1000, every: TimeSpan.Zero, stallAfter: 1000);
            var info = new UpdateInfo("v99.0", new Version(99, 0, 0), stalling.Url, "", "https://example.invalid/release");
            var mark = HeadlessRecords.Log.Mark;
            DriveWhilePumping(main, () =>
            {
                for (var attempt = 1; attempt <= 2; attempt++)
                {
                    main.BeginInvoke(() => installNow.Invoke(main, [info]));
                    Check(WaitFor(() => QuestionSays("Could not download or stage the update"), TimeSpan.FromSeconds(15)),
                        attempt == 1
                            ? "a stalled download must end with the person told it failed"
                            : "THE DEAD BUTTON: after a stalled download, Install now must really try again, not be ignored until a restart");
                    Check(main.Invoke(() => engine.Execute("answer OK")).StartsWith("ok", StringComparison.Ordinal), "the message must be answerable");
                    Thread.Sleep(300);
                }
            }, TimeSpan.FromSeconds(60));
            var log = HeadlessRecords.Log.Since(mark);
            Check(stalling.Requests == 2, $"the release must have been asked for twice, once per Install now ({stalling.Requests})");
            Check(!log.Any(l => l.Contains("install already in progress", StringComparison.Ordinal)), "and the second must not be turned away");
            return "a stalled download tells the person it failed, and Install now then really tries again";
        }
        finally
        {
            try { form?.Dispose(); } catch { /* teardown */ }
            Windowless.SetActiveForTest(false);
            CuePlayer.GloballyMuted = restoreMuted;
            RemSoundUpdater.DownloadStallLimit = restoreLimit;
            RemSoundUpdater.InstallDirForTest = restoreDir;
            RemoveNewStages(stagesBefore);
            try { Directory.Delete(install, recursive: true); } catch { /* temp */ }
        }
    }
}
